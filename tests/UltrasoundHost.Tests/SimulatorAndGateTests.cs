using System.Threading.Channels;
using UltrasoundHost.Contracts;
using UltrasoundHost.Plugins.Device.Simulator;
using UltrasoundHost.Plugins.Processing.Basic;

namespace UltrasoundHost.Tests;

internal static class SimulatorHelpers
{
    // Unpaced, finite: the device produces exactly `frames` frames as fast as the channel accepts them.
    public static IDevice Open(int channels, int frames, int seed = 1, int adcBits = 16)
    {
        return new SimulatorDeviceFactory().Create(new SimulatorOptions
        {
            ReceiveChannels = channels,
            MaxFrames = frames,
            Paced = false,
            Seed = seed,
            AdcBits = adcBits,
        });
    }

    public static async Task<List<AcquisitionFrame>> CollectAsync(IDevice device, AcquisitionParameters parameters, int count)
    {
        await device.ApplyAsync(parameters);
        Channel<AcquisitionFrame> channel = Channel.CreateBounded<AcquisitionFrame>(
            new BoundedChannelOptions(count + 2) { FullMode = BoundedChannelFullMode.Wait });
        await device.StartAsync(channel.Writer);

        var frames = new List<AcquisitionFrame>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (frames.Count < count)
        {
            frames.Add(await channel.Reader.ReadAsync(timeout.Token));
        }

        await device.StopAsync();
        return frames;
    }

    public static async Task<AcquisitionFrame> OneFrameAsync(AcquisitionParameters parameters, int channels = 2, int seed = 1)
    {
        await using IDevice device = Open(channels, 1, seed);
        List<AcquisitionFrame> frames = await CollectAsync(device, parameters, 1);
        return frames[0];
    }

    // Kept out of async methods: spans cannot live in them.
    public static bool SameData(AcquisitionFrame a, AcquisitionFrame b) => a.Data.Span.SequenceEqual(b.Data.Span);

    public static void DisposeAll(IEnumerable<AcquisitionFrame> frames)
    {
        foreach (AcquisitionFrame frame in frames)
        {
            frame.Dispose();
        }
    }
}

public class SimulatorDeviceTests
{
    [Fact]
    public async Task Frames_have_the_reported_shape_sequence_and_parameter_version()
    {
        await using IDevice device = SimulatorHelpers.Open(channels: 4, frames: 3);
        List<AcquisitionFrame> frames = await SimulatorHelpers.CollectAsync(device, new AcquisitionParameters { Version = 1 }, 3);
        try
        {
            Assert.Equal(3, frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                AcquisitionFrame frame = frames[i];
                Assert.Equal(FrameKind.AScan, frame.Kind);
                Assert.Equal(1, frame.Tx);
                Assert.Equal(4, frame.Rx);
                Assert.Equal(500, frame.Samples);
                Assert.Equal(i, frame.Sequence);
                Assert.Equal(1, frame.ParamVersion);
                Assert.Equal(50e6, frame.SampleRateHz);
            }
        }
        finally
        {
            SimulatorHelpers.DisposeAll(frames);
        }
    }

    [Fact]
    public async Task The_device_reports_capabilities_that_match_its_configuration()
    {
        await using IDevice single = SimulatorHelpers.Open(1, 1);
        Assert.Equal(IntRange.Exactly(1), single.Capabilities.ReceiveChannels);
        Assert.Equal(ProbeTopologies.Single, single.Capabilities.Topologies);
        Assert.Equal(1, single.Probe.ElementCount);

        await using IDevice array = SimulatorHelpers.Open(16, 1);
        Assert.Equal(IntRange.Exactly(16), array.Capabilities.ElementCount);
        Assert.Equal(ProbeTopologies.Linear, array.Capabilities.Topologies);
        Assert.Equal(16, array.Probe.ElementCount);
    }

    [Fact]
    public async Task The_same_seed_gives_the_same_data_and_a_different_seed_does_not()
    {
        var p = new AcquisitionParameters { Version = 1 };
        AcquisitionFrame a = await SimulatorHelpers.OneFrameAsync(p, seed: 7);
        AcquisitionFrame b = await SimulatorHelpers.OneFrameAsync(p, seed: 7);
        AcquisitionFrame c = await SimulatorHelpers.OneFrameAsync(p, seed: 8);
        try
        {
            Assert.True(SimulatorHelpers.SameData(a, b));
            Assert.False(SimulatorHelpers.SameData(a, c));
        }
        finally
        {
            SimulatorHelpers.DisposeAll(new[] { a, b, c });
        }
    }

    [Fact]
    public async Task Parameter_versions_must_increase()
    {
        await using IDevice device = SimulatorHelpers.Open(1, 1);
        var v1 = new AcquisitionParameters { Version = 1 };
        await device.ApplyAsync(v1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => device.ApplyAsync(v1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.ApplyAsync(v1 with { Version = 0 }));
        await device.ApplyAsync(v1.Revise(p => p with { GainDb = 3 }));
    }

    [Fact]
    public async Task Parameters_outside_the_device_limits_are_refused()
    {
        await using IDevice device = SimulatorHelpers.Open(1, 1);
        var tooLong = new AcquisitionParameters { Version = 1, SamplesPerRecord = 20000, Gate = new GateConfig(2e-6, 5.5e-6, 0.2) };
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => device.ApplyAsync(tooLong));
        Assert.Contains("samples per record", ex.Message);
    }

    [Fact]
    public async Task Starting_without_parameters_or_twice_is_refused()
    {
        await using IDevice device = SimulatorHelpers.Open(1, 0);
        Channel<AcquisitionFrame> channel = Channel.CreateBounded<AcquisitionFrame>(2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => device.StartAsync(channel.Writer));

        await device.ApplyAsync(new AcquisitionParameters { Version = 1 });
        await device.StartAsync(channel.Writer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.StartAsync(channel.Writer));

        await device.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        while (channel.Reader.TryRead(out AcquisitionFrame? leftover))
        {
            leftover.Dispose();
        }
    }

    [Fact]
    public async Task Stop_does_not_hang_when_the_consumer_stops_reading()
    {
        // Capacity 1 with back-pressure: the producer ends up blocked inside WriteAsync.
        await using IDevice device = new SimulatorDeviceFactory().Create(new SimulatorOptions { Paced = false });
        await device.ApplyAsync(new AcquisitionParameters { Version = 1 });
        Channel<AcquisitionFrame> channel = Channel.CreateBounded<AcquisitionFrame>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });
        await device.StartAsync(channel.Writer);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        AcquisitionFrame first = await channel.Reader.ReadAsync(timeout.Token);
        first.Dispose();

        await device.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        while (channel.Reader.TryRead(out AcquisitionFrame? leftover))
        {
            leftover.Dispose();
        }
    }

    [Fact]
    public async Task A_new_gain_shows_up_in_frames_with_the_new_version()
    {
        await using IDevice device = new SimulatorDeviceFactory().Create(new SimulatorOptions { Paced = false });
        var v1 = new AcquisitionParameters { Version = 1 };
        await device.ApplyAsync(v1);
        Channel<AcquisitionFrame> channel = Channel.CreateBounded<AcquisitionFrame>(
            new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.Wait });
        await device.StartAsync(channel.Writer);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await device.ApplyAsync(v1.Revise(p => p with { GainDb = 6 }));

        // Frames already queued still carry version 1; sooner or later version 2 arrives.
        int seenVersion = 0;
        while (seenVersion < 2)
        {
            using AcquisitionFrame frame = await channel.Reader.ReadAsync(timeout.Token);
            seenVersion = frame.ParamVersion;
        }

        Assert.Equal(2, seenVersion);
        await device.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        while (channel.Reader.TryRead(out AcquisitionFrame? leftover))
        {
            leftover.Dispose();
        }
    }

    [Fact]
    public async Task Fault_injection_produces_sequence_gaps_and_overflow_flags()
    {
        await using IDevice device = new SimulatorDeviceFactory().Create(new SimulatorOptions
        {
            ReceiveChannels = 1,
            MaxFrames = 6,
            Paced = false,
            Seed = 1,
            SequenceGapAtFrame = 3,
            SequenceGapSize = 2,
            InjectDeviceOverflowEvery = 2,
        });

        await device.ApplyAsync(new AcquisitionParameters { Version = 1 });
        var channel = Channel.CreateBounded<AcquisitionFrame>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
        await device.StartAsync(channel.Writer);

        var frames = new List<AcquisitionFrame>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (frames.Count < 6)
        {
            frames.Add(await channel.Reader.ReadAsync(timeout.Token));
        }

        await device.StopAsync();
        try
        {
            Assert.Equal(new long[] { 0, 1, 2, 5, 6, 7 }, frames.Select(frame => frame.Sequence).ToArray());
            Assert.Equal(FrameFlags.None, frames[0].Flags);
            Assert.Equal(FrameFlags.DeviceOverflow, frames[2].Flags);
            Assert.Equal(FrameFlags.DeviceOverflow, frames[4].Flags);
        }
        finally
        {
            SimulatorHelpers.DisposeAll(frames);
        }
    }

    [Fact]
    public async Task Fault_injection_can_emit_unknown_parameter_versions()
    {
        await using IDevice device = new SimulatorDeviceFactory().Create(new SimulatorOptions
        {
            ReceiveChannels = 1,
            MaxFrames = 1,
            Paced = false,
            Seed = 1,
            EmitUnknownParamVersion = true,
        });

        List<AcquisitionFrame> frames = await SimulatorHelpers.CollectAsync(device, new AcquisitionParameters { Version = 7 }, 1);
        try
        {
            Assert.Equal(107, frames[0].ParamVersion);
        }
        finally
        {
            frames[0].Dispose();
        }
    }

    [Fact]
    public void Options_validate_the_channel_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulatorOptions { ReceiveChannels = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulatorOptions { ReceiveChannels = 5000 }.Validate());
    }
}

public class GatePeakProcessorTests
{
    private static readonly GatePeakProcessor Processor = new();

    [Fact]
    public async Task The_defect_gate_finds_the_defect_at_the_simulated_depth_on_every_channel()
    {
        var p = new AcquisitionParameters { Version = 1 };
        AcquisitionFrame frame = await SimulatorHelpers.OneFrameAsync(p, channels: 2);
        try
        {
            ProcessedFrame result = Processor.Process(frame, p);

            Assert.Equal(2, result.Gates.Count);
            GateResult ch0 = result.Gates[0];
            GateResult ch1 = result.Gates[1];

            Assert.True(ch0.Triggered);
            Assert.True(ch1.Triggered);
            Assert.InRange(ch0.PeakAmplitude, 0.22, 0.32);
            Assert.InRange(ch0.SoundPathMeters, 0.0100 - 0.0004, 0.0100 + 0.0004);
            Assert.InRange(ch1.SoundPathMeters, 0.0105 - 0.0004, 0.0105 + 0.0004);
        }
        finally
        {
            frame.Dispose();
        }
    }

    [Fact]
    public async Task Moving_the_gate_onto_the_back_wall_reports_the_thickness()
    {
        var p = new AcquisitionParameters { Version = 1, Gate = new GateConfig(5.5e-6, 8.5e-6, 0.3) };
        AcquisitionFrame frame = await SimulatorHelpers.OneFrameAsync(p, channels: 1);
        try
        {
            GateResult gate = Processor.Process(frame, p).Gates[0];

            Assert.True(gate.Triggered);
            Assert.InRange(gate.PeakAmplitude, 0.42, 0.55);
            Assert.InRange(gate.SoundPathMeters, 0.020 - 0.0004, 0.020 + 0.0004);
        }
        finally
        {
            frame.Dispose();
        }
    }

    [Fact]
    public async Task The_threshold_decides_the_alarm_and_gain_changes_the_amplitude()
    {
        var quiet = new AcquisitionParameters { Version = 1, Gate = new GateConfig(2e-6, 5.5e-6, 0.5) };
        var loud = quiet with { GainDb = 6 };

        AcquisitionFrame quietFrame = await SimulatorHelpers.OneFrameAsync(quiet, channels: 1);
        AcquisitionFrame loudFrame = await SimulatorHelpers.OneFrameAsync(loud, channels: 1);
        try
        {
            GateResult atZeroDb = Processor.Process(quietFrame, quiet).Gates[0];
            GateResult atSixDb = Processor.Process(loudFrame, loud).Gates[0];

            Assert.False(atZeroDb.Triggered);
            Assert.True(atSixDb.Triggered);
            Assert.InRange(atSixDb.PeakAmplitude / atZeroDb.PeakAmplitude, 1.9, 2.1);
        }
        finally
        {
            quietFrame.Dispose();
            loudFrame.Dispose();
        }
    }

    [Fact]
    public async Task A_frame_from_an_older_parameter_version_is_rejected()
    {
        var p = new AcquisitionParameters { Version = 1 };
        AcquisitionFrame frame = await SimulatorHelpers.OneFrameAsync(p, channels: 1);
        try
        {
            AcquisitionParameters newer = p.Revise(x => x with { GainDb = 3 });
            Assert.Throws<InvalidOperationException>(() => Processor.Process(frame, newer));
        }
        finally
        {
            frame.Dispose();
        }
    }

    [Fact]
    public void A_negative_peak_counts_by_magnitude_and_full_scale_is_one()
    {
        using AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 1, 100);
        frame.Data.Span.Clear();
        frame.Span(0, 0)[50] = short.MinValue;
        frame.Span(0, 0)[20] = 1000;
        frame.ParamVersion = 1;
        frame.SampleRateHz = 50e6;
        var p = new AcquisitionParameters { Version = 1, SamplesPerRecord = 100, Gate = new GateConfig(0, 2e-6, 0.9) };

        GateResult gate = Processor.Process(frame, p).Gates[0];

        Assert.Equal(50, gate.PeakIndex);
        Assert.Equal(1.0, gate.PeakAmplitude);
        Assert.True(gate.Triggered);
        Assert.Equal(50 / 50e6, gate.PeakTimeSeconds, 12);
    }

    [Fact]
    public void A_gate_outside_the_record_has_no_peak()
    {
        using AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 1, 100);
        frame.Data.Span.Clear();
        frame.ParamVersion = 1;
        frame.SampleRateHz = 50e6;
        var p = new AcquisitionParameters { Version = 1, Gate = new GateConfig(10e-6, 12e-6, 0.2) };

        GateResult gate = Processor.Process(frame, p).Gates[0];

        Assert.Equal(-1, gate.PeakIndex);
        Assert.False(gate.Triggered);
        Assert.Equal(0.0, gate.PeakAmplitude);
    }

    [Fact]
    public void The_start_delay_shifts_gate_and_depth_correctly()
    {
        using AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 1, 100);
        frame.Data.Span.Clear();
        frame.Span(0, 0)[10] = 20000;
        frame.ParamVersion = 1;
        frame.SampleRateHz = 50e6;
        frame.StartDelaySeconds = 4e-6;
        var p = new AcquisitionParameters
        {
            Version = 1,
            SamplesPerRecord = 100,
            StartDelaySeconds = 4e-6,
            Gate = new GateConfig(4e-6, 6e-6, 0.2),
        };

        GateResult gate = Processor.Process(frame, p).Gates[0];

        // Sample 10 is 4 us + 10 / 50 MHz = 4.2 us after the pulse; depth = 5900 m/s * 4.2 us / 2.
        Assert.Equal(10, gate.PeakIndex);
        Assert.Equal(4.2e-6, gate.PeakTimeSeconds, 12);
        Assert.Equal(5900 * 4.2e-6 / 2, gate.SoundPathMeters, 9);
    }

    [Fact]
    public void Only_a_scan_frames_are_handled()
    {
        using AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.Fmc, 2, 2, 16);
        frame.SampleRateHz = 50e6;
        Assert.Throws<NotSupportedException>(() => Processor.Process(frame, new AcquisitionParameters()));
    }

    [Fact]
    public async Task Adc_bit_depth_does_not_change_the_amplitude_percentage()
    {
        var p = new AcquisitionParameters { Version = 1 };
        var factory = new SimulatorDeviceFactory();

        await using IDevice device16 = factory.Create(new SimulatorOptions { ReceiveChannels = 1, MaxFrames = 1, Paced = false, Seed = 1 });
        List<AcquisitionFrame> frames16 = await SimulatorHelpers.CollectAsync(device16, p, 1);

        await using IDevice device12 = factory.Create(new SimulatorOptions { ReceiveChannels = 1, MaxFrames = 1, Paced = false, Seed = 1, AdcBits = 12 });
        List<AcquisitionFrame> frames12 = await SimulatorHelpers.CollectAsync(device12, p, 1);

        try
        {
            Assert.Equal(16, frames16[0].Adc.Bits);
            Assert.Equal(12, frames12[0].Adc.Bits);

            GateResult at16Bits = Processor.Process(frames16[0], p).Gates[0];
            GateResult at12Bits = Processor.Process(frames12[0], p).Gates[0];

            Assert.True(at16Bits.Triggered);
            Assert.True(at12Bits.Triggered);
            Assert.InRange(at12Bits.PeakAmplitude, at16Bits.PeakAmplitude - 0.02, at16Bits.PeakAmplitude + 0.02);
        }
        finally
        {
            frames16[0].Dispose();
            frames12[0].Dispose();
        }
    }
}
