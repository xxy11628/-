using System.Threading.Channels;
using UltrasoundHost.Contracts;
using UltrasoundHost.Plugins.Device.Simulator;

namespace UltrasoundHost.Tests;

[CollectionDefinition("FramePool", DisableParallelization = true)]
public sealed class FramePoolCollection
{
}

/// <summary>
/// Reusable device contract suite (07 §4.2). A new device plugin joins by inheriting this class and
/// providing the device and parameters.
/// </summary>
[Collection("FramePool")]
public abstract class DeviceContractTestsBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    protected abstract Task<IDevice> OpenAsync();

    protected abstract AcquisitionParameters CreateParameters();

    protected abstract AcquisitionParameters CreateOutOfRangeParameters();

    [Fact]
    public async Task DC1_capabilities_are_self_consistent()
    {
        IDevice device = await OpenAsync();
        try
        {
            DeviceCapabilities capabilities = device.Capabilities;
            Assert.True(capabilities.ElementCount.IsValid);
            Assert.True(capabilities.ReceiveChannels.IsValid);
            Assert.True(capabilities.SamplesPerRecord.IsValid);
            Assert.True(capabilities.ReceiveChannels.Min >= 1);
            Assert.Empty(capabilities.Adc.Validate());
            Assert.NotNull(device.Probe);
            Assert.True(device.Probe.ElementCount >= 1);
        }
        finally
        {
            await device.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task DC2_out_of_range_parameters_are_rejected()
    {
        IDevice device = await OpenAsync();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => device.ApplyAsync(CreateOutOfRangeParameters()));
        }
        finally
        {
            await device.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task DC3_frames_carry_the_applied_parameter_version_and_never_a_newer_one()
    {
        IDevice device = await OpenAsync();
        Channel<AcquisitionFrame>? channel = null;
        var frames = new List<AcquisitionFrame>();
        try
        {
            AcquisitionParameters first = CreateParameters();
            await device.ApplyAsync(first);
            channel = CreateChannel(8);
            await device.StartAsync(channel.Writer);

            using var timeout = new CancellationTokenSource(Timeout);
            while (frames.Count < 3)
            {
                frames.Add(await channel.Reader.ReadAsync(timeout.Token));
            }

            AcquisitionParameters second = first.Revise(previous => previous with { GainDb = 3 });
            await device.ApplyAsync(second);

            int attempts = 0;
            while (!frames.Any(frame => frame.ParamVersion == 2) && attempts < 200)
            {
                frames.Add(await channel.Reader.ReadAsync(timeout.Token));
                attempts++;
            }

            Assert.Contains(frames, frame => frame.ParamVersion == 2);
            Assert.All(frames, frame => Assert.InRange(frame.ParamVersion, 1, 2));
        }
        finally
        {
            await CleanupAsync(device, channel, frames);
        }
    }

    [Fact]
    public async Task DC4_stop_is_idempotent_and_a_second_start_is_a_wiring_error()
    {
        IDevice device = await OpenAsync();
        Channel<AcquisitionFrame>? channel = null;
        try
        {
            await device.ApplyAsync(CreateParameters());
            channel = CreateChannel(8);
            await device.StartAsync(channel.Writer);

            await Assert.ThrowsAsync<InvalidOperationException>(() => device.StartAsync(channel.Writer));
            await device.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await device.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await CleanupAsync(device, channel, Array.Empty<AcquisitionFrame>());
        }
    }

    [Fact]
    public async Task DC5_frames_blocked_in_a_full_channel_are_returned_to_the_pool()
    {
        long baseline = AcquisitionFrame.OutstandingFrames;
        IDevice device = await OpenAsync();
        Channel<AcquisitionFrame>? channel = null;
        var frames = new List<AcquisitionFrame>();
        try
        {
            await device.ApplyAsync(CreateParameters());
            channel = CreateChannel(1);
            await device.StartAsync(channel.Writer);

            using var timeout = new CancellationTokenSource(Timeout);
            AcquisitionFrame first = await channel.Reader.ReadAsync(timeout.Token);
            first.Dispose();
        }
        finally
        {
            await CleanupAsync(device, channel, frames);
        }

        Assert.Equal(baseline, AcquisitionFrame.OutstandingFrames);
    }

    [Fact]
    public async Task DC6_sequence_numbers_are_strictly_increasing_with_step_one()
    {
        IDevice device = await OpenAsync();
        Channel<AcquisitionFrame>? channel = null;
        var frames = new List<AcquisitionFrame>();
        try
        {
            await device.ApplyAsync(CreateParameters());
            channel = CreateChannel(8);
            await device.StartAsync(channel.Writer);

            using var timeout = new CancellationTokenSource(Timeout);
            while (frames.Count < 5)
            {
                frames.Add(await channel.Reader.ReadAsync(timeout.Token));
            }

            for (int index = 1; index < frames.Count; index++)
            {
                Assert.Equal(frames[index - 1].Sequence + 1, frames[index].Sequence);
            }
        }
        finally
        {
            await CleanupAsync(device, channel, frames);
        }
    }

    [Fact]
    public async Task DC7_frame_shape_and_adc_match_capabilities_and_parameters()
    {
        IDevice device = await OpenAsync();
        Channel<AcquisitionFrame>? channel = null;
        var frames = new List<AcquisitionFrame>();
        try
        {
            AcquisitionParameters parameters = CreateParameters();
            await device.ApplyAsync(parameters);
            channel = CreateChannel(4);
            await device.StartAsync(channel.Writer);

            using var timeout = new CancellationTokenSource(Timeout);
            frames.Add(await channel.Reader.ReadAsync(timeout.Token));

            AcquisitionFrame frame = frames[0];
            Assert.Equal(FrameKind.AScan, frame.Kind);
            Assert.Equal(1, frame.Tx);
            Assert.InRange(frame.Rx, device.Capabilities.ReceiveChannels.Min, device.Capabilities.ReceiveChannels.Max);
            Assert.Equal(parameters.SamplesPerRecord, frame.Samples);
            Assert.Equal(device.Capabilities.Adc.Bits, frame.Adc.Bits);
        }
        finally
        {
            await CleanupAsync(device, channel, frames);
        }
    }

    [Fact]
    public async Task DC8_cancellation_stops_the_device_within_the_timeout()
    {
        IDevice device = await OpenAsync();
        Channel<AcquisitionFrame>? channel = null;
        var frames = new List<AcquisitionFrame>();
        try
        {
            await device.ApplyAsync(CreateParameters());
            channel = CreateChannel(4);
            using var cancellation = new CancellationTokenSource();
            await device.StartAsync(channel.Writer, cancellation.Token);
            cancellation.Cancel();
            await device.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await CleanupAsync(device, channel, frames);
        }
    }

    [Fact]
    public async Task DC9_dispose_while_running_is_safe()
    {
        long baseline = AcquisitionFrame.OutstandingFrames;
        IDevice device = await OpenAsync();
        Channel<AcquisitionFrame>? channel = null;
        var frames = new List<AcquisitionFrame>();
        try
        {
            await device.ApplyAsync(CreateParameters());
            channel = CreateChannel(4);
            await device.StartAsync(channel.Writer);
            await device.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (channel is not null)
            {
                while (channel.Reader.TryRead(out AcquisitionFrame? leftover))
                {
                    leftover.Dispose();
                }
            }

            foreach (AcquisitionFrame frame in frames)
            {
                frame.Dispose();
            }
        }

        Assert.Equal(baseline, AcquisitionFrame.OutstandingFrames);
    }

    private static Channel<AcquisitionFrame> CreateChannel(int capacity) =>
        Channel.CreateBounded<AcquisitionFrame>(new BoundedChannelOptions(Math.Max(2, capacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    private static async Task CleanupAsync(IDevice device, Channel<AcquisitionFrame>? channel, IEnumerable<AcquisitionFrame> frames)
    {
        try
        {
            await device.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException)
        {
            // The test may already have stopped the device.
        }

        if (channel is not null)
        {
            while (channel.Reader.TryRead(out AcquisitionFrame? leftover))
            {
                leftover.Dispose();
            }
        }

        foreach (AcquisitionFrame frame in frames)
        {
            frame.Dispose();
        }

        await device.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public sealed class SimulatorDeviceContractTests : DeviceContractTestsBase
{
    protected override Task<IDevice> OpenAsync() =>
        Task.FromResult<IDevice>(new SimulatorDeviceFactory().Create(new SimulatorOptions
        {
            ReceiveChannels = 2,
            Paced = false,
            Seed = 1,
        }));

    protected override AcquisitionParameters CreateParameters() => new AcquisitionParameters { Version = 1, ActiveReceiveChannels = 1 };

    protected override AcquisitionParameters CreateOutOfRangeParameters() =>
        new AcquisitionParameters { Version = 1, SamplesPerRecord = 20000 };
}
