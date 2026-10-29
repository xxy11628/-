using Microsoft.Extensions.DependencyInjection;
using UltrasoundHost.Application;
using UltrasoundHost.Contracts;
using UltrasoundHost.Plugins.Device.Simulator;
using UltrasoundHost.Plugins.Processing.Basic;

namespace UltrasoundHost.Tests;

/// <summary>07 §5 frame-leak counting and DP7/DP9 stop scenarios.</summary>
[Collection("FramePool")]
public class FrameLeakTests
{
    [Fact]
    public async Task DP9_starting_and_stopping_many_times_returns_all_frames()
    {
        long baseline = AcquisitionFrame.OutstandingFrames;
        var session = new ShellSession(
            new IPlugin[] { new SimulatorPlugin(), new GatePeakPlugin() },
            options: new SessionOptions { Paced = false });

        try
        {
            await session.InitializeAsync();
            await session.OpenDeviceAsync(2);

            for (int iteration = 0; iteration < 100; iteration++)
            {
                var parameters = new AcquisitionParameters
                {
                    Version = iteration + 1,
                    ActiveReceiveChannels = 2,
                };

                await session.StartAcquisitionAsync(parameters);
                await SessionWait.UntilAsync(() => session.FramesProcessed >= 1, "a frame in iteration " + iteration);
                await session.StopAcquisitionAsync();

                Assert.False(session.IsAcquiring);
                Assert.Null(session.LastError);
            }
        }
        finally
        {
            await session.DisposeAsync();
        }

        Assert.Equal(baseline, AcquisitionFrame.OutstandingFrames);
    }

    [Fact]
    public async Task DP7_a_device_failure_is_reported_and_the_session_stops_cleanly()
    {
        long baseline = AcquisitionFrame.OutstandingFrames;
        var options = new SessionOptions
        {
            Paced = false,
            DeviceSettings = new Dictionary<string, string> { ["throwAfterFrames"] = "2" },
        };
        var session = new ShellSession(
            new IPlugin[] { new SimulatorPlugin(), new GatePeakPlugin() },
            options: options);

        try
        {
            await session.InitializeAsync();
            await session.OpenDeviceAsync(1);
            await session.StartAcquisitionAsync(new AcquisitionParameters { Version = 1 });
            await SessionWait.UntilAsync(() => session.FramesProcessed >= 2, "two frames before the injected failure");

            await session.StopAcquisitionAsync();

            Assert.False(session.IsAcquiring);
            Assert.NotNull(session.LastError);
            Assert.Contains("injected", session.LastError!.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await session.DisposeAsync();
        }

        Assert.Equal(baseline, AcquisitionFrame.OutstandingFrames);
    }

    [Fact]
    public async Task DP7_a_processor_failure_is_counted_and_reported()
    {
        long baseline = AcquisitionFrame.OutstandingFrames;
        var session = new ShellSession(
            new IPlugin[] { new SimulatorPlugin(), new ThrowingProcessorPlugin() },
            options: new SessionOptions { Paced = false });

        try
        {
            await session.InitializeAsync();
            await session.OpenDeviceAsync(1);
            await session.StartAcquisitionAsync(new AcquisitionParameters { Version = 1 });
            await SessionWait.UntilAsync(() => session.Counters.Inspection.Dropped > 0, "a dropped inspection frame");

            await session.StopAcquisitionAsync();

            Assert.False(session.IsAcquiring);
            Assert.True(session.Counters.InspectionIncomplete);
            Assert.NotNull(session.LastError);
            Assert.Contains("injected processor", session.LastError!.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await session.DisposeAsync();
        }

        Assert.Equal(baseline, AcquisitionFrame.OutstandingFrames);
    }
}

/// <summary>Processing plugin whose processor always throws, so the inspection consumer must count and report it.</summary>
internal sealed class ThrowingProcessorPlugin : IPlugin, IProcessorContribution
{
    public PluginManifest Manifest { get; } = new PluginManifest("processing.throwing", new Version(0, 1, 0), PluginKind.Processing, "故障处理插件");

    public IFrameProcessor CreateProcessor() => new ThrowingProcessor();

    public void Register(IServiceCollection services)
    {
    }
}

internal sealed class ThrowingProcessor : IFrameProcessor
{
    public string Name => "throwing";

    public ProcessedFrame Process(AcquisitionFrame frame, AcquisitionParameters parameters) =>
        throw new InvalidOperationException("injected processor failure");
}
