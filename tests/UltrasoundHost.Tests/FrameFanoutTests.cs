using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using UltrasoundHost.Application;
using UltrasoundHost.Contracts;

namespace UltrasoundHost.Tests;

/// <summary>Unit tests for the fanout/loss accounting (05 §3–§5, DP2–DP4).</summary>
public class FrameFanoutTests
{
    private static AcquisitionFrame MakeFrame(long sequence, FrameFlags flags = FrameFlags.None)
    {
        AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 1, 16);
        frame.Sequence = sequence;
        frame.ParamVersion = 1;
        frame.SampleRateHz = 50e6;
        frame.Flags = flags;
        return frame;
    }

    private static (ChannelWriter<AcquisitionFrame> Writer, FrameFanout Fanout, FramePath Inspection, FramePath Display, LossCountersBuilder Counters) CreatePipeline(
        int inspectionCapacity = 64,
        int displayCapacity = 64)
    {
        Channel<AcquisitionFrame> source = Channel.CreateBounded<AcquisitionFrame>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        var counters = new LossCountersBuilder();
        var inspection = new FramePath(FramePathKind.Inspection, inspectionCapacity, dropOldest: false, counters);
        var display = new FramePath(FramePathKind.Display, displayCapacity, dropOldest: true, counters);
        var fanout = new FrameFanout(source.Reader, new[] { inspection, display }, counters);
        return (source.Writer, fanout, inspection, display, counters);
    }

    private static void DrainAndCount(FramePath path, LossCountersBuilder counters)
    {
        while (path.Reader.TryRead(out AcquisitionFrame? frame))
        {
            frame.Dispose();
            counters.AddProcessed(path.Kind);
        }
    }

    [Fact]
    public async Task A_slow_display_path_drops_counted_frames_while_inspection_is_untouched()
    {
        (ChannelWriter<AcquisitionFrame> writer, FrameFanout fanout, FramePath inspection, FramePath display, LossCountersBuilder counters) =
            CreatePipeline(inspectionCapacity: 64, displayCapacity: 2);

        for (int sequence = 0; sequence < 10; sequence++)
        {
            await writer.WriteAsync(MakeFrame(sequence));
        }

        writer.TryComplete();
        await fanout.RunAsync(CancellationToken.None);

        // Two display frames are still queued; no display consumer ran during the feed, so 8 were dropped.
        DrainAndCount(display, counters);
        DrainAndCount(inspection, counters);

        LossCounters snapshot = counters.Snapshot();
        Assert.Equal(8, snapshot.Display.Dropped);
        Assert.Equal(2, snapshot.Display.Processed);
        Assert.Equal(0, snapshot.Inspection.Dropped);
        Assert.Equal(snapshot.Received, snapshot.Display.Offered);
        Assert.Equal(snapshot.Display.Offered, snapshot.Display.Processed + snapshot.Display.Dropped + snapshot.Display.UnknownVersion);
        Assert.False(snapshot.InspectionIncomplete);
    }

    [Fact]
    public async Task An_inspection_overrun_is_counted_and_marks_the_result_incomplete()
    {
        (ChannelWriter<AcquisitionFrame> writer, FrameFanout fanout, FramePath inspection, FramePath display, LossCountersBuilder counters) =
            CreatePipeline(inspectionCapacity: 1, displayCapacity: 64);

        for (int sequence = 0; sequence < 5; sequence++)
        {
            await writer.WriteAsync(MakeFrame(sequence));
        }

        writer.TryComplete();
        await fanout.RunAsync(CancellationToken.None);

        DrainAndCount(display, counters);
        DrainAndCount(inspection, counters);

        LossCounters snapshot = counters.Snapshot();
        Assert.Equal(4, snapshot.Inspection.Dropped);
        Assert.True(snapshot.InspectionIncomplete);
    }

    [Fact]
    public async Task Sequence_gaps_and_device_overflow_are_counted()
    {
        (ChannelWriter<AcquisitionFrame> writer, FrameFanout fanout, FramePath inspection, FramePath display, LossCountersBuilder counters) =
            CreatePipeline();

        await writer.WriteAsync(MakeFrame(0));
        await writer.WriteAsync(MakeFrame(1));
        await writer.WriteAsync(MakeFrame(5));
        await writer.WriteAsync(MakeFrame(6, FrameFlags.DeviceOverflow));

        writer.TryComplete();
        await fanout.RunAsync(CancellationToken.None);

        DrainAndCount(display, counters);
        DrainAndCount(inspection, counters);

        LossCounters snapshot = counters.Snapshot();
        Assert.Equal(4, snapshot.Received);
        Assert.Equal(3, snapshot.SequenceGaps);
        Assert.Equal(1, snapshot.DeviceOverflow);
        Assert.Equal(0, snapshot.SequenceViolations);
        Assert.True(snapshot.InspectionIncomplete);
    }

    [Fact]
    public async Task A_duplicate_sequence_is_a_violation_and_reports_an_error()
    {
        Channel<AcquisitionFrame> source = Channel.CreateBounded<AcquisitionFrame>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        var counters = new LossCountersBuilder();
        var inspection = new FramePath(FramePathKind.Inspection, 16, dropOldest: false, counters);
        var display = new FramePath(FramePathKind.Display, 16, dropOldest: true, counters);
        Exception? reported = null;
        var fanout = new FrameFanout(
            source.Reader,
            new[] { inspection, display },
            counters,
            error => reported = error);

        await source.Writer.WriteAsync(MakeFrame(10));
        await source.Writer.WriteAsync(MakeFrame(10));
        source.Writer.TryComplete();
        await fanout.RunAsync(CancellationToken.None);

        DrainAndCount(display, counters);
        DrainAndCount(inspection, counters);

        LossCounters snapshot = counters.Snapshot();
        Assert.Equal(1, snapshot.SequenceViolations);
        Assert.NotNull(reported);
    }
}

/// <summary>A device that keeps sending frames with a parameter version the session never registered.</summary>
internal sealed class UnknownVersionDevicePlugin : IPlugin, IDeviceFactoryContribution
{
    public const string DeviceType = "unknown-version";

    private readonly UnknownVersionDeviceFactory _factory = new();

    public PluginManifest Manifest { get; } = new PluginManifest("device.unknown-version", new Version(0, 1, 0), PluginKind.Device, "未知版本设备");

    public IDeviceFactory Factory => _factory;

    public void Register(IServiceCollection services) => services.AddSingleton<IDeviceFactory>(_factory);
}

internal sealed class UnknownVersionDeviceFactory : IDeviceFactory
{
    public string DeviceType => UnknownVersionDevicePlugin.DeviceType;

    public Task<IDevice> OpenAsync(DeviceOpenOptions options, CancellationToken cancellationToken = default) =>
        Task.FromResult<IDevice>(new UnknownVersionDevice());
}

internal sealed class UnknownVersionDevice : IDevice
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _task;

    public string Id => "unknown-version-1";

    public DeviceCapabilities Capabilities { get; } = new()
    {
        Model = "UnknownVersion",
        ElementCount = IntRange.Exactly(1),
        ReceiveChannels = IntRange.Exactly(1),
        Schemes = AcquisitionSchemes.AScan,
        Topologies = ProbeTopologies.Single,
        SamplesPerRecord = new IntRange(16, 16384),
        MaxSampleRateHz = 100e6,
        MaxPrfHz = 10000,
    };

    public ProbeModel Probe { get; } = ProbeModel.Single();

    public Task ApplyAsync(AcquisitionParameters parameters, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StartAsync(ChannelWriter<AcquisitionFrame> sink, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = _cts.Token;
            _task = Task.Run(() => RunAsync(sink, token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            task = _task;
            cts = _cts;
            _task = null;
            _cts = null;
        }

        if (task is null || cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private static async Task RunAsync(ChannelWriter<AcquisitionFrame> sink, CancellationToken cancellationToken)
    {
        long sequence = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 1, 32);
                bool handedOver = false;
                try
                {
                    frame.Sequence = sequence++;
                    frame.ParamVersion = 99;
                    frame.SampleRateHz = 50e6;
                    await sink.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                    handedOver = true;
                }
                catch (ChannelClosedException)
                {
                    return;
                }
                finally
                {
                    if (!handedOver)
                    {
                        frame.Dispose();
                    }
                }

                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
