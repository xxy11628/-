using System.Threading.Channels;
using UltrasoundHost.Application;
using UltrasoundHost.Contracts;
using UltrasoundHost.Core;
using UltrasoundHost.Plugins.Device.SerialUdp;
using UltrasoundHost.Plugins.Processing.Basic;

namespace UltrasoundHost.Tests;

internal static class SerialUdpTestOptions
{
    public static Dictionary<string, string> LoopbackSettings(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["loopback"] = "true",
            ["paced"] = "false",
            ["maxChannels"] = "16",
        };

        foreach ((string key, string value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }
}

[Collection("FramePool")]
public sealed class SerialUdpDeviceContractTests : DeviceContractTestsBase
{
    protected override Task<IDevice> OpenAsync() =>
        new SerialUdpDeviceFactory().OpenAsync(new DeviceOpenOptions { Settings = SerialUdpTestOptions.LoopbackSettings() });

    protected override AcquisitionParameters CreateParameters() =>
        new AcquisitionParameters { Version = 1, ActiveReceiveChannels = 1 };

    protected override AcquisitionParameters CreateOutOfRangeParameters() =>
        new AcquisitionParameters { Version = 1, SamplesPerRecord = 20000 };
}

[Collection("FramePool")]
public class SerialUdpLoopbackTests
{
    [Fact]
    public async Task Loopback_frames_carry_protocol_metadata_and_step_one_sequences()
    {
        await using IDevice device = await new SerialUdpDeviceFactory().OpenAsync(
            new DeviceOpenOptions { Settings = SerialUdpTestOptions.LoopbackSettings() });
        var parameters = new AcquisitionParameters { Version = 1, ActiveReceiveChannels = 1 };
        await device.ApplyAsync(parameters);

        Channel<AcquisitionFrame> channel = Channel.CreateBounded<AcquisitionFrame>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
        await device.StartAsync(channel.Writer);

        var frames = new List<AcquisitionFrame>();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (frames.Count < 3)
            {
                frames.Add(await channel.Reader.ReadAsync(timeout.Token));
            }

            Assert.Equal(new long[] { 0, 1, 2 }, frames.Select(frame => frame.Sequence).ToArray());
            Assert.All(frames, frame =>
            {
                Assert.Equal(1, frame.ParamVersion);
                Assert.Equal(FrameKind.AScan, frame.Kind);
                Assert.Equal(1, frame.Rx);
                Assert.Equal(16, frame.Adc.Bits);
                Assert.Equal(parameters.SamplesPerRecord, frame.Samples);
            });
        }
        finally
        {
            await device.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            while (channel.Reader.TryRead(out AcquisitionFrame? leftover))
            {
                leftover.Dispose();
            }

            foreach (AcquisitionFrame frame in frames)
            {
                frame.Dispose();
            }
        }
    }
}

[Collection("FramePool")]
public class SerialUdpSessionTests
{
    private static ShellSession CreateSession(Dictionary<string, string> settings) => new(
        new IPlugin[] { new SerialUdpPlugin(), new GatePeakPlugin() },
        options: new SessionOptions
        {
            DeviceType = SerialUdpOptions.DeviceType,
            Paced = false,
            DeviceSettings = settings,
        });

    [Fact]
    public async Task The_loopback_board_runs_through_the_session_and_triggers_the_gate()
    {
        long baseline = AcquisitionFrame.OutstandingFrames;
        var session = CreateSession(SerialUdpTestOptions.LoopbackSettings());
        try
        {
            await session.InitializeAsync();
            SessionCheckResult check = await session.OpenDeviceAsync(1);
            Assert.True(check.AllSatisfied);

            await session.StartAcquisitionAsync(new AcquisitionParameters { Version = 1, ActiveReceiveChannels = 1 });
            await SessionWait.UntilAsync(
                () => session.FramesProcessed >= 3 && session.LatestInspection is not null,
                "loopback frames");

            ProcessedFrame inspection = session.LatestInspection!;
            Assert.Single(inspection.Gates);
            Assert.True(inspection.Gates[0].Triggered);

            await session.StopAcquisitionAsync();
            Assert.False(session.IsAcquiring);
            Assert.Null(session.LastError);
            Assert.False(session.Counters.InspectionIncomplete);
        }
        finally
        {
            await session.DisposeAsync();
        }

        Assert.Equal(baseline, AcquisitionFrame.OutstandingFrames);
    }

    [Fact]
    public async Task Dropped_datagrams_show_up_as_sequence_gaps()
    {
        long baseline = AcquisitionFrame.OutstandingFrames;
        var session = CreateSession(SerialUdpTestOptions.LoopbackSettings(("dropEvery", "2")));
        try
        {
            await session.InitializeAsync();
            await session.OpenDeviceAsync(1);
            await session.StartAcquisitionAsync(new AcquisitionParameters { Version = 1, ActiveReceiveChannels = 1 });
            await SessionWait.UntilAsync(() => session.Counters.SequenceGaps > 0, "a sequence gap");

            await session.StopAcquisitionAsync();
            Assert.True(session.Counters.InspectionIncomplete);
        }
        finally
        {
            await session.DisposeAsync();
        }

        Assert.Equal(baseline, AcquisitionFrame.OutstandingFrames);
    }
}
