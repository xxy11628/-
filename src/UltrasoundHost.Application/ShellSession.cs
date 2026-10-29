using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using UltrasoundHost.Contracts;
using UltrasoundHost.Core;

namespace UltrasoundHost.Application;

/// <summary>
/// Owns the plugin host, the connected device and the data path. Contains no UI types: both shells
/// (CLI and WPF) drive it and the WPF <c>--selftest</c> exercises the same code.
/// <para>
/// Data path (05): one reader takes frames from the device channel and hands them to the inspection
/// and display paths through <see cref="FrameFanout"/>. Every frame is either processed or counted as
/// dropped/unknown; inspection never drops silently. The inspection consumer processes samples with
/// the parameters the frame was acquired under (<see cref="ParameterHistory"/>); the display consumer
/// copies a snapshot for the UI and may drop old frames, counted per drop.
/// </para>
/// </summary>
public sealed class ShellSession : IAsyncDisposable
{
    private readonly IPlugin[] _plugins;
    private readonly HostCapabilities _hostCapabilities;
    private readonly SessionOptions _options;
    private readonly ServiceCollection _services = new();
    private readonly object _gate = new();
    private readonly ParameterHistory _parameterHistory;

    private ServiceProvider? _provider;
    private PluginHost? _host;
    private IFrameProcessor? _processor;
    private IDevice? _device;
    private Channel<AcquisitionFrame>? _deviceChannel;
    private FramePath? _inspectionPath;
    private FramePath? _displayPath;
    private LossCountersBuilder? _counters;
    private CancellationTokenSource? _cancellation;
    private Task? _fanoutTask;
    private Task? _inspectionTask;
    private Task? _displayTask;
    private AcquisitionParameters? _parameters;
    private SessionCheckResult? _sessionCheck;
    private FrameSnapshot? _latestSnapshot;
    private ProcessedFrame? _latestInspection;
    private DisplayRequest _displayRequest = DisplayRequest.Default;
    private int _selectedChannel;
    private Exception? _lastError;
    private bool _disposed;

    public ShellSession(IEnumerable<IPlugin> plugins, HostCapabilities? hostCapabilities = null, SessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        _plugins = plugins.ToArray();
        _hostCapabilities = hostCapabilities ?? new HostCapabilities();
        _options = options ?? new SessionOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.SourceQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.InspectionQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.RecordQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.DisplayQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ParameterHistoryDepth, 1);
        _parameterHistory = new ParameterHistory(_options.ParameterHistoryDepth);
    }

    /// <summary>Raised on the display consumer thread after a new waveform snapshot was published.</summary>
    public event Action<FrameSnapshot>? SnapshotPublished;

    /// <summary>Raised on the inspection consumer thread after a frame was processed.</summary>
    public event Action<ProcessedFrame>? InspectionCompleted;

    public SessionOptions Options => _options;

    public IReadOnlyList<PluginStatus> Statuses => _host?.Statuses ?? Array.Empty<PluginStatus>();

    /// <summary>Views contributed by the plugins that actually started.</summary>
    public IReadOnlyList<ViewDescriptor> Views { get; private set; } = Array.Empty<ViewDescriptor>();

    public SessionCheckResult? SessionCheck => _sessionCheck;

    public string? DeviceId => _device?.Id;

    public DeviceCapabilities? DeviceCapabilities => _device?.Capabilities;

    public ProbeModel? Probe => _device?.Probe;

    /// <summary>Parameters currently applied to the device. Frames are processed with their own version.</summary>
    public AcquisitionParameters? Parameters => Volatile.Read(ref _parameters);

    /// <summary>Latest waveform snapshot produced by the display path.</summary>
    public FrameSnapshot? LatestSnapshot => Volatile.Read(ref _latestSnapshot);

    /// <summary>Union of the display needs of the active views; the display consumer copies only this.</summary>
    public DisplayRequest DisplayRequest => Volatile.Read(ref _displayRequest);

    /// <summary>Selects the receive channel that views declaring "Selected" want to show.</summary>
    public void SelectDisplayChannel(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 0);
        _selectedChannel = channel;
        Volatile.Write(ref _displayRequest, DisplayRequest.Create(Views, channel));
    }

    /// <summary>Latest inspection result. May lag the display snapshot when the display path drops frames.</summary>
    public ProcessedFrame? LatestInspection => Volatile.Read(ref _latestInspection);

    /// <summary>Thread-safe snapshot of every frame's fate since acquisition started.</summary>
    public LossCounters Counters => _counters?.Snapshot() ?? LossCounters.Empty;

    public long FramesProcessed => Counters.Inspection.Processed;

    /// <summary>Compatibility metric: frames whose parameter version was absent from the history.</summary>
    public long StaleDropped => Counters.StaleDropped;

    /// <summary>True once the inspection path knowingly missed a frame in the current acquisition.</summary>
    public bool InspectionIncomplete => Counters.InspectionIncomplete;

    public Exception? LastError => Volatile.Read(ref _lastError);

    public bool IsAcquiring
    {
        get
        {
            lock (_gate)
            {
                return _cancellation is not null;
            }
        }
    }

    /// <summary>Builds the host, starts the plugins and collects the active contributions.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_host is not null)
        {
            throw new InvalidOperationException("会话已经初始化");
        }

        var host = new PluginHost(_plugins, _hostCapabilities);
        host.Build(_services);
        _host = host;
        _provider = _services.BuildServiceProvider();

        await host.InitializeAsync(_provider, cancellationToken).ConfigureAwait(false);
        await host.StartAsync(cancellationToken).ConfigureAwait(false);

        // Rule R11: capabilities are enumerated through the host, never resolved from the DI
        // container, so a plugin that ended up Rejected/Faulted cannot hand over a processor or view.
        _processor = host.GetActive<IProcessorContribution>()
            .Select(contribution => contribution.CreateProcessor())
            .FirstOrDefault();
        Views = host.GetActive<IViewContribution>().SelectMany(contribution => contribution.Views).ToArray();
        Volatile.Write(ref _displayRequest, DisplayRequest.Create(Views, _selectedChannel));
    }

    /// <summary>
    /// Opens the configured device and runs the session check. The device stays open when the check
    /// fails so the caller can show why; <see cref="StartAcquisitionAsync"/> refuses to start then.
    /// </summary>
    public async Task<SessionCheckResult> OpenDeviceAsync(int channels, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ThrowIfDisposed();
        if (IsAcquiring)
        {
            throw new InvalidOperationException("请先停止采集，再更换设备");
        }

        if (_host is null)
        {
            throw new InvalidOperationException("请先初始化会话");
        }

        IDeviceFactory factory = _host.GetActive<IDeviceFactoryContribution>()
            .Select(contribution => contribution.Factory)
            .FirstOrDefault(candidate => string.Equals(candidate.DeviceType, _options.DeviceType, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("没有已启动的设备插件提供“" + _options.DeviceType + "”设备");

        var settings = new Dictionary<string, string>(_options.DeviceSettings, StringComparer.OrdinalIgnoreCase)
        {
            ["channels"] = channels.ToString(CultureInfo.InvariantCulture),
            ["paced"] = _options.Paced ? "true" : "false",
        };

        IDevice device = await factory.OpenAsync(new DeviceOpenOptions { Settings = settings }, cancellationToken).ConfigureAwait(false);
        IDevice? previous;
        lock (_gate)
        {
            previous = _device;
            _device = device;
        }

        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        Volatile.Write(ref _lastError, null);
        SessionCheckResult check = _host!.CheckSession(device.Capabilities);
        _sessionCheck = check;
        return check;
    }

    /// <summary>
    /// Applies the first set of parameters and starts the data path. The parameters are registered in
    /// the history before they reach the device, so every frame can be processed with its own version.
    /// </summary>
    public async Task StartAcquisitionAsync(AcquisitionParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ThrowIfDisposed();

        IDevice device = _device ?? throw new InvalidOperationException("请先打开设备");
        IFrameProcessor processor = _processor
            ?? throw new InvalidOperationException("没有已激活的帧处理器（处理插件可能被拒绝或故障）");

        if (_sessionCheck is not { AllSatisfied: true })
        {
            string reasons = _sessionCheck is null
                ? "尚未执行会话检查"
                : string.Join("；", _sessionCheck.Items.Where(item => !item.Satisfied).Select(item => item.PluginId + "：" + string.Join("、", item.Unmet)));
            throw new InvalidOperationException("会话检查未通过，未开始采集：" + reasons);
        }

        IReadOnlyList<string> problems = parameters.ValidateAgainst(device.Capabilities);
        if (problems.Count > 0)
        {
            throw new ArgumentException("参数不合法：" + string.Join("；", problems), nameof(parameters));
        }

        if (IsAcquiring)
        {
            throw new InvalidOperationException("采集已经在运行");
        }

        _parameterHistory.Add(parameters);
        await device.ApplyAsync(parameters, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _parameters, parameters);
        Volatile.Write(ref _latestSnapshot, null);
        Volatile.Write(ref _latestInspection, null);
        Volatile.Write(ref _lastError, null);

        var counters = new LossCountersBuilder();
        var deviceChannel = Channel.CreateBounded<AcquisitionFrame>(new BoundedChannelOptions(_options.SourceQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        var inspectionPath = new FramePath(FramePathKind.Inspection, _options.InspectionQueueCapacity, dropOldest: false, counters);
        var displayPath = new FramePath(FramePathKind.Display, _options.DisplayQueueCapacity, dropOldest: true, counters);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var fanout = new FrameFanout(
            deviceChannel.Reader,
            new[] { inspectionPath, displayPath },
            counters,
            error => Volatile.Write(ref _lastError, error));

        _counters = counters;
        Task fanoutTask = Task.Run(() => fanout.RunAsync(cancellation.Token), CancellationToken.None);
        Task inspectionTask = Task.Run(
            () => RunInspectionAsync(inspectionPath.Reader, processor, counters, cancellation.Token),
            CancellationToken.None);
        Task displayTask = Task.Run(
            () => RunDisplayAsync(displayPath.Reader, counters, cancellation.Token),
            CancellationToken.None);

        try
        {
            await device.StartAsync(deviceChannel.Writer, cancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            cancellation.Cancel();
            deviceChannel.Writer.TryComplete();
            await IgnoreAllAsync(fanoutTask, inspectionTask, displayTask).ConfigureAwait(false);
            Drain(inspectionPath.Reader);
            Drain(displayPath.Reader);
            Drain(deviceChannel.Reader);
            cancellation.Dispose();
            throw;
        }

        lock (_gate)
        {
            _deviceChannel = deviceChannel;
            _inspectionPath = inspectionPath;
            _displayPath = displayPath;
            _cancellation = cancellation;
            _fanoutTask = fanoutTask;
            _inspectionTask = inspectionTask;
            _displayTask = displayTask;
        }
    }

    /// <summary>
    /// Applies new parameters while running. The version must increase and is recorded in the history
    /// before the device sees it, so in-flight frames keep being processed with their own version
    /// instead of being dropped as "stale".
    /// </summary>
    public async Task ApplyParametersAsync(AcquisitionParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ThrowIfDisposed();

        IDevice device = _device ?? throw new InvalidOperationException("请先打开设备");
        IReadOnlyList<string> problems = parameters.ValidateAgainst(device.Capabilities);
        if (problems.Count > 0)
        {
            throw new ArgumentException("参数不合法：" + string.Join("；", problems), nameof(parameters));
        }

        _parameterHistory.Add(parameters);
        await device.ApplyAsync(parameters, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _parameters, parameters);
    }

    /// <summary>Stops the device, drains the data path and releases every frame still queued.</summary>
    public async Task StopAcquisitionAsync()
    {
        Channel<AcquisitionFrame>? deviceChannel;
        FramePath? inspectionPath;
        FramePath? displayPath;
        CancellationTokenSource? cancellation;
        Task? fanoutTask;
        Task? inspectionTask;
        Task? displayTask;
        IDevice? device = _device;

        lock (_gate)
        {
            deviceChannel = _deviceChannel;
            inspectionPath = _inspectionPath;
            displayPath = _displayPath;
            cancellation = _cancellation;
            fanoutTask = _fanoutTask;
            inspectionTask = _inspectionTask;
            displayTask = _displayTask;
            _deviceChannel = null;
            _inspectionPath = null;
            _displayPath = null;
            _cancellation = null;
            _fanoutTask = null;
            _inspectionTask = null;
            _displayTask = null;
        }

        if (cancellation is null || fanoutTask is null || inspectionTask is null || displayTask is null)
        {
            return;
        }

        cancellation.Cancel();
        if (device is not null)
        {
            try
            {
                await device.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _lastError, ex);
            }
        }

        deviceChannel?.Writer.TryComplete();
        try
        {
            await fanoutTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastError, ex);
        }

        try
        {
            await Task.WhenAll(inspectionTask, displayTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            Volatile.Write(ref _lastError, ex);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastError, ex);
        }

        Drain(inspectionPath, _counters);
        Drain(displayPath, _counters);
        Drain(deviceChannel?.Reader);
        cancellation.Dispose();

        // 07 §5: after a clean stop every rented frame must be back in the pool.
        long outstandingFrames = AcquisitionFrame.OutstandingFrames;
        if (outstandingFrames != 0)
        {
            Volatile.Write(ref _lastError, new InvalidOperationException(
                "停止后仍有 " + outstandingFrames + " 帧未归还到内存池（窗口泄露，见 07 §5）"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await StopAcquisitionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastError, ex);
        }

        if (_device is not null)
        {
            try
            {
                await _device.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _lastError, ex);
            }

            _device = null;
        }

        if (_host is not null)
        {
            try
            {
                await _host.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _lastError, ex);
            }

            _host = null;
        }

        if (_provider is not null)
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
            _provider = null;
        }
    }

    private async Task RunInspectionAsync(
        ChannelReader<AcquisitionFrame> reader,
        IFrameProcessor processor,
        LossCountersBuilder counters,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out AcquisitionFrame? frame))
                {
                    using (frame)
                    {
                        if ((frame.Flags & FrameFlags.Truncated) != 0)
                        {
                            counters.AddDropped(FramePathKind.Inspection);
                            continue;
                        }

                        if (!_parameterHistory.TryGet(frame.ParamVersion, out AcquisitionParameters parameters))
                        {
                            counters.AddUnknownVersion(FramePathKind.Inspection);
                            continue;
                        }

                        try
                        {
                            ProcessedFrame processed = processor.Process(frame, parameters);
                            counters.AddProcessed(FramePathKind.Inspection);
                            Volatile.Write(ref _latestInspection, processed);
                            try
                            {
                                InspectionCompleted?.Invoke(processed);
                            }
                            catch
                            {
                                // A subscriber must not be able to kill the inspection path.
                            }
                        }
                        catch (Exception ex)
                        {
                            Volatile.Write(ref _lastError, ex);
                            counters.AddDropped(FramePathKind.Inspection);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastError, ex);
        }
    }

    private async Task RunDisplayAsync(
        ChannelReader<AcquisitionFrame> reader,
        LossCountersBuilder counters,
        CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        double lastPublishedSeconds = double.NegativeInfinity;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out AcquisitionFrame? frame))
                {
                    using (frame)
                    {
                        if (!_parameterHistory.TryGet(frame.ParamVersion, out AcquisitionParameters parameters))
                        {
                            counters.AddUnknownVersion(FramePathKind.Display);
                            continue;
                        }

                        DisplayRequest request = Volatile.Read(ref _displayRequest);
                        if (request.MaxRefreshHz is double maxRefreshHz && maxRefreshHz > 0)
                        {
                            double now = clock.Elapsed.TotalSeconds;
                            if (now - lastPublishedSeconds < 1.0 / maxRefreshHz)
                            {
                                // Refresh throttled by the views: the frame is consumed but not shown.
                                counters.AddDropped(FramePathKind.Display);
                                continue;
                            }

                            lastPublishedSeconds = now;
                        }

                        FrameSnapshot snapshot = FrameSnapshot.Capture(frame, parameters, request);
                        counters.AddProcessed(FramePathKind.Display);
                        Volatile.Write(ref _latestSnapshot, snapshot);

                        try
                        {
                            SnapshotPublished?.Invoke(snapshot);
                        }
                        catch
                        {
                            // A subscriber must not be able to kill the display path.
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastError, ex);
        }
    }

    private static void Drain(ChannelReader<AcquisitionFrame>? reader)
    {
        if (reader is null)
        {
            return;
        }

        while (reader.TryRead(out AcquisitionFrame? leftover))
        {
            leftover.Dispose();
        }
    }

    /// <summary>Frames still queued when the path stops were not processed: count them as dropped.</summary>
    private static void Drain(FramePath? path, LossCountersBuilder? counters)
    {
        if (path is null)
        {
            return;
        }

        while (path.Reader.TryRead(out AcquisitionFrame? leftover))
        {
            leftover.Dispose();
            counters?.AddDropped(path.Kind);
        }
    }

    private static async Task IgnoreAllAsync(params Task[] tasks)
    {
        foreach (Task task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Cleanup after a failed start: keep the original exception.
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ShellSession));
        }
    }
}
