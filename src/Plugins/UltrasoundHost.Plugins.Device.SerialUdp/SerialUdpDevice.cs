using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using UltrasoundHost.Contracts;

namespace UltrasoundHost.Plugins.Device.SerialUdp;

/// <summary>
/// Device plugin implementation for the draft serial/UDP protocol: control commands travel over an
/// <see cref="IControlTransport"/> (serial), frame datagrams arrive over an <see cref="IDataTransport"/>
/// (UDP). Only the loopback board is wired in M3; the real transports fail with a clear message.
/// </summary>
internal sealed class SerialUdpDevice : IDevice
{
    private readonly IControlTransport _control;
    private readonly IDataTransport _data;
    private readonly SerialUdpOptions _options;
    private readonly byte[] _controlBuffer = new byte[2048];
    private readonly byte[] _datagramBuffer = new byte[SerialUdpProtocol.HeaderSize + SerialUdpProtocol.MaxDatagramPayload];
    private readonly object _gate = new();

    private AcquisitionParameters? _parameters;
    private CancellationTokenSource? _cancellation;
    private Task? _receiveTask;
    private int _lastParameterVersion = -1;

    public SerialUdpDevice(
        IControlTransport control,
        IDataTransport data,
        DeviceCapabilities capabilities,
        ProbeModel probe,
        SerialUdpOptions options)
    {
        _control = control;
        _data = data;
        _options = options;
        Capabilities = capabilities;
        Probe = probe;
        Id = "serial-udp-" + options.DataPort;
    }

    public string Id { get; }

    public DeviceCapabilities Capabilities { get; }

    public ProbeModel Probe { get; }

    public async Task ApplyAsync(AcquisitionParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        IReadOnlyList<string> problems = parameters.ValidateAgainst(Capabilities);
        if (problems.Count > 0)
        {
            throw new ArgumentException("invalid parameters: " + string.Join("; ", problems), nameof(parameters));
        }

        if (parameters.Version <= _lastParameterVersion)
        {
            throw new InvalidOperationException(
                "parameter version " + parameters.Version + " is not higher than the applied version " + _lastParameterVersion);
        }

        await SendControlAsync(
            SerialUdpProtocol.CommandConfigure,
            SerialUdpProtocol.EncodeConfigure(parameters),
            cancellationToken).ConfigureAwait(false);

        _parameters = parameters;
        _lastParameterVersion = parameters.Version;
    }

    public async Task StartAsync(ChannelWriter<AcquisitionFrame> sink, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            if (_receiveTask is not null)
            {
                throw new InvalidOperationException("the device is already started");
            }

            if (_parameters is null)
            {
                throw new InvalidOperationException("apply parameters before starting");
            }
        }

        await SendControlAsync(SerialUdpProtocol.CommandStart, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task receiveTask = Task.Run(() => ReceiveLoopAsync(sink, cancellation.Token), CancellationToken.None);
        lock (_gate)
        {
            _cancellation = cancellation;
            _receiveTask = receiveTask;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? receiveTask;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            receiveTask = _receiveTask;
            cancellation = _cancellation;
            _receiveTask = null;
            _cancellation = null;
        }

        if (receiveTask is null || cancellation is null)
        {
            return;
        }

        try
        {
            await SendControlAsync(SerialUdpProtocol.CommandStop, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The board may already be gone; stopping the receiver below is what matters.
        }

        cancellation.Cancel();
        try
        {
            await receiveTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (ReferenceEquals(_control, _data))
        {
            await _data.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await _data.DisposeAsync().ConfigureAwait(false);
            await _control.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(ChannelWriter<AcquisitionFrame> sink, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int length = await _data.ReceiveAsync(_datagramBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (length <= 0)
                {
                    continue;
                }

                AcquisitionFrame? frame = TryCreateFrame(length);
                if (frame is null)
                {
                    continue; // corrupted or reserved layout: drop, the next sequence number will show a gap
                }

                bool handedOver = false;
                try
                {
                    await sink.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                    handedOver = true;
                }
                finally
                {
                    if (!handedOver)
                    {
                        frame.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ChannelClosedException)
        {
            // The session stopped reading.
        }
    }

    /// <summary>Synchronous because spans must not cross an await in this language version.</summary>
    private AcquisitionFrame? TryCreateFrame(int length)
    {
        ReadOnlySpan<byte> datagram = _datagramBuffer.AsSpan(0, length);
        if (!SerialUdpProtocol.TryDecodeDataFrame(datagram, out SerialUdpDataHeader header, out ReadOnlySpan<byte> payload))
        {
            return null;
        }

        if (header.Tx < 1 || header.Rx < 1 || header.Samples < 1)
        {
            return null;
        }

        AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, header.Tx, header.Rx, header.Samples);
        try
        {
            MemoryMarshal.Cast<byte, short>(payload).CopyTo(frame.Data.Span);
            frame.Sequence = header.Sequence;
            frame.ParamVersion = header.ParamVersion;
            frame.Adc = header.Adc;
            frame.Flags = header.Flags;
            frame.SampleRateHz = _parameters?.SampleRateHz ?? 0;
            frame.StartDelaySeconds = _parameters?.StartDelaySeconds ?? 0;
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private async Task SendControlAsync(byte command, byte[] payload, CancellationToken cancellationToken)
    {
        byte[] request = SerialUdpProtocol.EncodeControlRequest(command, payload);
        await _control.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        int length = await _control.ReadAsync(_controlBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        ValidateControlResponse(_controlBuffer, length, command);
    }

    /// <summary>Synchronous so the response span never crosses an await.</summary>
    private static void ValidateControlResponse(byte[] buffer, int length, byte command)
    {
        if (!SerialUdpProtocol.TryDecodeControlResponse(
                buffer.AsSpan(0, length),
                out byte responseCommand,
                out byte status,
                out ReadOnlySpan<byte> responsePayload))
        {
            throw new InvalidOperationException("the board returned a malformed control response");
        }

        if (responseCommand != command || status != SerialUdpProtocol.StatusOk)
        {
            throw new InvalidOperationException(
                "board rejected command 0x" + command.ToString("X2") + " with status " + status + " (" + responsePayload.Length + " payload bytes)");
        }
    }
}

/// <summary>Creates serial/UDP devices. Real transports are intentionally not implemented yet (M3).</summary>
public sealed class SerialUdpDeviceFactory : IDeviceFactory
{
    public string DeviceType => SerialUdpOptions.DeviceType;

    public async Task<IDevice> OpenAsync(DeviceOpenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        SerialUdpOptions parsed = SerialUdpOptions.FromSettings(options);
        if (!parsed.Loopback)
        {
            throw new NotSupportedException(
                "the real serial/UDP transports are not implemented until the board and protocol v0.1 are confirmed; "
                + "use loopback=true for now (docs/protocol/v0.1.md)");
        }

        var board = new LoopbackBoard(parsed);
        try
        {
            byte[] identify = SerialUdpProtocol.EncodeControlRequest(
                SerialUdpProtocol.CommandIdentify,
                ReadOnlySpan<byte>.Empty);
            await board.WriteAsync(identify, cancellationToken).ConfigureAwait(false);

            var responseBuffer = new byte[2048];
            int length = await board.ReadAsync(responseBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            DeviceCapabilities capabilities = DecodeIdentifyResponse(responseBuffer, length);
            ProbeModel probe = capabilities.ElementCount.Max <= 1
                ? ProbeModel.Single()
                : ProbeModel.Linear(Math.Min(16, capabilities.ElementCount.Max), 0.6e-3);
            return new SerialUdpDevice(board, board, capabilities, probe, parsed);
        }
        catch
        {
            await board.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Synchronous so the response span never crosses an await.</summary>
    private static DeviceCapabilities DecodeIdentifyResponse(byte[] buffer, int length)
    {
        if (!SerialUdpProtocol.TryDecodeControlResponse(
                buffer.AsSpan(0, length),
                out byte command,
                out byte status,
                out ReadOnlySpan<byte> payload)
            || command != SerialUdpProtocol.CommandIdentify
            || status != SerialUdpProtocol.StatusOk)
        {
            throw new InvalidOperationException("the loopback board did not answer Identify");
        }

        return SerialUdpProtocol.DecodeCapabilities(payload);
    }
}

/// <summary>Device plugin: serial control channel + Ethernet data channel (protocol v0.1 draft).</summary>
public sealed class SerialUdpPlugin : IPlugin, IDeviceFactoryContribution
{
    public const string PluginId = "device.serial-udp";

    private readonly SerialUdpDeviceFactory _factory = new();

    public PluginManifest Manifest { get; } = new PluginManifest(PluginId, new Version(0, 1, 0), PluginKind.Device, "串口+网口设备")
    {
        DeviceProvides = new DeviceCapabilities
        {
            Model = "SerialUdp",
            ElementCount = new IntRange(1, 4096),
            ReceiveChannels = new IntRange(1, 4096),
            Schemes = AcquisitionSchemes.AScan,
            Topologies = ProbeTopologies.Single | ProbeTopologies.Linear,
            SamplesPerRecord = new IntRange(16, 16384),
            MaxSampleRateHz = 100e6,
            MaxPrfHz = 10000,
            ThroughputMBps = 1000,
            Adc = AdcFormat.Default,
        },
    };

    public IDeviceFactory Factory => _factory;

    public void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services) =>
        services.AddSingleton<IDeviceFactory>(_factory);
}
