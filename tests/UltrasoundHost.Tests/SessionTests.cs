using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using UltrasoundHost.Application;
using UltrasoundHost.Contracts;
using UltrasoundHost.Core;
using UltrasoundHost.Plugins.Device.Simulator;
using UltrasoundHost.Plugins.Processing.Basic;
using UltrasoundHost.Plugins.View.Basic;

namespace UltrasoundHost.Tests;

internal static class SessionWait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMilliseconds = 10000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("timed out waiting for " + what);
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}

/// <summary>A plugin with a manifest and an optional registration action, for session tests.</summary>
internal sealed class SimplePlugin : IPlugin
{
    private readonly Action<IServiceCollection>? _register;

    public SimplePlugin(PluginManifest manifest, Action<IServiceCollection>? register = null)
    {
        Manifest = manifest;
        _register = register;
    }

    public PluginManifest Manifest { get; }

    public void Register(IServiceCollection services) => _register?.Invoke(services);
}

internal sealed class FailingDevicePlugin : IPlugin, IDeviceFactoryContribution
{
    public const string DeviceType = "failing";

    private readonly FailingDeviceFactory _factory = new();

    public PluginManifest Manifest { get; } = new PluginManifest("device.failing", new Version(0, 1, 0), PluginKind.Device, "故障设备");

    public IDeviceFactory Factory => _factory;

    public void Register(IServiceCollection services) => services.AddSingleton<IDeviceFactory>(_factory);
}

internal sealed class FailingDeviceFactory : IDeviceFactory
{
    public string DeviceType => FailingDevicePlugin.DeviceType;

    public Task<IDevice> OpenAsync(DeviceOpenOptions options, CancellationToken cancellationToken = default) =>
        Task.FromResult<IDevice>(new FailingDevice());
}

/// <summary>Applies parameters fine but refuses to start, so the session must roll back cleanly.</summary>
internal sealed class FailingDevice : IDevice
{
    public string Id => "failing-1";

    public DeviceCapabilities Capabilities { get; } = new()
    {
        Model = "Failing",
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

    public Task StartAsync(ChannelWriter<AcquisitionFrame> sink, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("injected start failure");

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Contributes a processor but faults in Start, so its processor must not be used.</summary>
internal sealed class FaultyProcessorPlugin : IPlugin, IProcessorContribution
{
    public const string PluginId = "processing.faulty";

    public PluginManifest Manifest { get; } = new PluginManifest(PluginId, new Version(0, 1, 0), PluginKind.Processing, "故障处理插件");

    public IFrameProcessor CreateProcessor() => new NoopProcessor();

    public void Register(IServiceCollection services)
    {
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("injected start failure");
}

/// <summary>Contributes a view but faults in Start, so its view must not be published.</summary>
internal sealed class FaultyViewPlugin : IPlugin, IViewContribution
{
    public const string PluginId = "view.faulty";

    public PluginManifest Manifest { get; } = new PluginManifest(PluginId, new Version(0, 1, 0), PluginKind.View, "故障视图插件");

    public IReadOnlyList<ViewDescriptor> Views { get; } = new[]
    {
        new ViewDescriptor("faulty.view", "故障视图", ViewKinds.AScan),
    };

    public void Register(IServiceCollection services)
    {
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("injected start failure");
}

public class SessionTests
{
    private static IPlugin[] SimulatorChain() => new IPlugin[] { new SimulatorPlugin(), new GatePeakPlugin() };

    /// <summary>Unpaced simulator: tests run as fast as the consumer accepts frames.</summary>
    private static SessionOptions FastOptions() => new SessionOptions { Paced = false };

    [Fact]
    public void Session_options_have_safe_defaults_and_validate_the_queue_capacity()
    {
        var options = new SessionOptions();

        Assert.Equal("simulator", options.DeviceType);
        Assert.True(options.Paced);
        Assert.Equal(8, options.DisplayQueueCapacity);
        Assert.Equal(64, options.InspectionQueueCapacity);
        Assert.Equal(64, options.RecordQueueCapacity);
        Assert.Equal(8, options.SourceQueueCapacity);
        Assert.Equal(16, options.ParameterHistoryDepth);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ShellSession(Array.Empty<IPlugin>(), null, new SessionOptions { InspectionQueueCapacity = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ShellSession(Array.Empty<IPlugin>(), null, new SessionOptions { ParameterHistoryDepth = 0 }));
    }

    [Fact]
    public async Task The_session_initializes_opens_the_device_and_passes_the_check()
    {
        await using var session = new ShellSession(SimulatorChain(), options: FastOptions());
        await session.InitializeAsync();

        Assert.All(session.Statuses, status => Assert.Equal(PluginState.Running, status.State));
        Assert.Contains(session.Statuses, status => status.Id == SimulatorPlugin.PluginId);

        SessionCheckResult check = await session.OpenDeviceAsync(2);

        Assert.True(check.AllSatisfied);
        Assert.NotNull(session.DeviceId);
        Assert.NotNull(session.DeviceCapabilities);
        Assert.True(session.DeviceCapabilities!.ReceiveChannels.Contains(2));
    }

    [Fact]
    public async Task Acquisition_processes_frames_and_publishes_triggered_gates()
    {
        await using var session = new ShellSession(SimulatorChain(), options: FastOptions());
        await session.InitializeAsync();
        await session.OpenDeviceAsync(2);

        int published = 0;
        session.SnapshotPublished += _ => Interlocked.Increment(ref published);
        await session.StartAcquisitionAsync(new AcquisitionParameters { Version = 1 });
        await SessionWait.UntilAsync(
            () => session.FramesProcessed >= 3 && session.LatestSnapshot is not null,
            "three processed frames and a display snapshot");

        ProcessedFrame inspection = session.LatestInspection ?? throw new InvalidOperationException("no inspection result");
        FrameSnapshot snapshot = session.LatestSnapshot ?? throw new InvalidOperationException("no display snapshot");
        Assert.Equal(1, inspection.ParamVersion);
        Assert.Equal(2, inspection.Gates.Count);
        Assert.All(inspection.Gates, gate => Assert.True(gate.Triggered));
        Assert.Equal(2, snapshot.ChannelCount);
        Assert.True(published >= 1);

        await session.StopAcquisitionAsync();
        Assert.False(session.IsAcquiring);
    }

    [Fact]
    public async Task Parameter_revision_switches_the_version_frames_carry()
    {
        await using var session = new ShellSession(SimulatorChain(), options: FastOptions());
        await session.InitializeAsync();
        await session.OpenDeviceAsync(1);

        var first = new AcquisitionParameters { Version = 1 };
        await session.StartAcquisitionAsync(first);
        await SessionWait.UntilAsync(() => session.FramesProcessed >= 3, "the first frames");

        AcquisitionParameters second = first.Revise(previous => previous with { GainDb = 6 });
        await session.ApplyParametersAsync(second);
        await SessionWait.UntilAsync(() => session.LatestInspection is { ParamVersion: 2 }, "an inspection result with version 2");

        Assert.Equal(2, session.Parameters?.Version);
        await session.StopAcquisitionAsync();
        Assert.False(session.IsAcquiring);

        // Frames acquired before the revision are processed with their own (v1) parameters: no
        // unknown-version drops and every offered frame is accounted for.
        LossCounters counters = session.Counters;
        Assert.Equal(0, counters.StaleDropped);
        Assert.Equal(0, counters.Inspection.UnknownVersion);
        Assert.Equal(counters.Received, counters.Inspection.Offered);
        Assert.Equal(counters.Inspection.Offered, counters.Inspection.Processed + counters.Inspection.Dropped + counters.Inspection.UnknownVersion);
    }

    [Fact]
    public async Task Start_is_refused_when_the_session_check_fails()
    {
        var needsFmc = new SimplePlugin(new PluginManifest("processing.needs-fmc", new Version(0, 1, 0), PluginKind.Processing, "需要 FMC")
        {
            DeviceNeeds = new DeviceRequirements { Schemes = AcquisitionSchemes.Fmc },
        });

        var plugins = new IPlugin[] { new SimulatorPlugin(), new GatePeakPlugin(), needsFmc };
        await using var session = new ShellSession(plugins, options: FastOptions());
        await session.InitializeAsync();

        SessionCheckResult check = await session.OpenDeviceAsync(1);
        Assert.False(check.AllSatisfied);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.StartAcquisitionAsync(new AcquisitionParameters { Version = 1 }));
        Assert.False(session.IsAcquiring);
    }

    [Fact]
    public async Task Parameters_outside_device_limits_are_rejected_before_acquisition_starts()
    {
        await using var session = new ShellSession(SimulatorChain(), options: FastOptions());
        await session.InitializeAsync();
        await session.OpenDeviceAsync(1);

        var tooManySamples = new AcquisitionParameters { Version = 1, SamplesPerRecord = 20000 };

        await Assert.ThrowsAsync<ArgumentException>(() => session.StartAcquisitionAsync(tooManySamples));
        Assert.False(session.IsAcquiring);
    }

    [Fact]
    public async Task A_device_that_fails_to_start_leaves_the_session_stopped()
    {
        var plugins = new IPlugin[] { new FailingDevicePlugin(), new GatePeakPlugin() };
        await using var session = new ShellSession(
            plugins,
            options: new SessionOptions { DeviceType = FailingDevicePlugin.DeviceType });

        await session.InitializeAsync();
        SessionCheckResult check = await session.OpenDeviceAsync(1);
        Assert.True(check.AllSatisfied);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.StartAcquisitionAsync(new AcquisitionParameters { Version = 1 }));

        Assert.False(session.IsAcquiring);
        await session.StopAcquisitionAsync();
        Assert.False(session.IsAcquiring);
    }

    [Fact]
    public async Task Stop_is_idempotent_and_dispose_while_acquiring_is_clean()
    {
        var session = new ShellSession(SimulatorChain(), options: FastOptions());
        var first = new AcquisitionParameters { Version = 1 };
        try
        {
            await session.InitializeAsync();
            await session.OpenDeviceAsync(2);
            await session.StartAcquisitionAsync(first);
            await SessionWait.UntilAsync(() => session.FramesProcessed >= 2, "two processed frames");

            await session.StopAcquisitionAsync();
            await session.StopAcquisitionAsync();
            Assert.False(session.IsAcquiring);

            await session.StartAcquisitionAsync(first.Revise(previous => previous with { GainDb = 3 }));
            await SessionWait.UntilAsync(() => session.FramesProcessed >= 3, "frames after a restart");
        }
        finally
        {
            await session.DisposeAsync();
        }

        Assert.False(session.IsAcquiring);
    }

    [Fact]
    public async Task A_faulted_processor_plugin_is_not_used_and_acquisition_is_refused()
    {
        var plugins = new IPlugin[] { new SimulatorPlugin(), new FaultyProcessorPlugin(), new BasicViewPlugin() };
        await using var session = new ShellSession(plugins, options: FastOptions());
        await session.InitializeAsync();

        Assert.Equal(PluginState.Faulted, session.Statuses.Single(status => status.Id == FaultyProcessorPlugin.PluginId).State);
        await session.OpenDeviceAsync(1);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.StartAcquisitionAsync(new AcquisitionParameters { Version = 1 }));

        Assert.Contains("帧处理器", error.Message);
        Assert.False(session.IsAcquiring);
    }

    [Fact]
    public async Task Views_from_a_faulted_plugin_are_not_published()
    {
        var plugins = new IPlugin[] { new SimulatorPlugin(), new GatePeakPlugin(), new FaultyViewPlugin() };
        await using var session = new ShellSession(plugins, options: FastOptions());
        await session.InitializeAsync();

        Assert.Equal(PluginState.Faulted, session.Statuses.Single(status => status.Id == FaultyViewPlugin.PluginId).State);
        Assert.Empty(session.Views);
    }

    [Fact]
    public async Task Normal_acquisition_keeps_the_loss_invariants()
    {
        await using var session = new ShellSession(SimulatorChain(), options: FastOptions());
        await session.InitializeAsync();
        await session.OpenDeviceAsync(2);
        await session.StartAcquisitionAsync(new AcquisitionParameters { Version = 1 });
        await SessionWait.UntilAsync(() => session.FramesProcessed >= 5, "five processed frames");
        await session.StopAcquisitionAsync();

        LossCounters counters = session.Counters;
        Assert.True(counters.Received >= 5);
        Assert.False(counters.InspectionIncomplete);

        foreach (FramePathKind kind in new[] { FramePathKind.Inspection, FramePathKind.Display })
        {
            PathCounters path = counters[kind];
            Assert.Equal(counters.Received, path.Offered);
            Assert.Equal(path.Offered, path.Processed + path.Dropped + path.UnknownVersion);
        }

        Assert.True(counters.Inspection.Processed >= 5);
        Assert.False(session.IsAcquiring);
    }

    [Fact]
    public async Task A_frame_with_an_unknown_parameter_version_marks_the_inspection_incomplete()
    {
        var plugins = new IPlugin[] { new UnknownVersionDevicePlugin(), new GatePeakPlugin() };
        await using var session = new ShellSession(
            plugins,
            options: new SessionOptions { DeviceType = UnknownVersionDevicePlugin.DeviceType });

        await session.InitializeAsync();
        await session.OpenDeviceAsync(1);
        await session.StartAcquisitionAsync(new AcquisitionParameters { Version = 1, ActiveReceiveChannels = 1 });
        await SessionWait.UntilAsync(() => session.Counters.Inspection.UnknownVersion > 0, "unknown-version frames");

        await session.StopAcquisitionAsync();

        LossCounters counters = session.Counters;
        Assert.True(counters.Inspection.UnknownVersion > 0);
        Assert.Equal(0, counters.Inspection.Processed);
        Assert.True(counters.InspectionIncomplete);
    }
}
