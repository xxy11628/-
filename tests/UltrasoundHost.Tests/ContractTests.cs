using System.Numerics;
using UltrasoundHost.Contracts;

namespace UltrasoundHost.Tests;

public class IntRangeTests
{
    [Fact]
    public void Overlap_and_contains_are_inclusive()
    {
        var range = new IntRange(16, 128);
        Assert.True(range.Contains(16));
        Assert.True(range.Contains(128));
        Assert.False(range.Contains(129));
        Assert.True(range.Overlaps(new IntRange(128, 256)));
        Assert.False(range.Overlaps(new IntRange(129, 256)));
        Assert.True(IntRange.Any.Overlaps(IntRange.Exactly(1)));
    }

    [Fact]
    public void Invalid_range_is_detected()
    {
        Assert.False(new IntRange(5, 4).IsValid);
        Assert.True(IntRange.Exactly(5).IsValid);
    }
}

public class AcquisitionFrameTests
{
    [Fact]
    public void Rent_gives_the_requested_shape_and_slices_records()
    {
        using AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 3, 500);
        Assert.Equal(1500, frame.Length);
        Assert.Equal(1500, frame.Data.Length);
        Assert.Equal(500, frame.Span(0, 2).Length);

        frame.Span(0, 2)[0] = 7;
        Assert.Equal((short)7, frame.Data.Span[1000]);
    }

    [Fact]
    public void Slices_follow_the_tx_rx_sample_layout()
    {
        using AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.Fmc, 2, 3, 4);
        frame.Span(1, 2)[3] = 99;
        // ((tx * Rx) + rx) * Samples + sample = ((1 * 3) + 2) * 4 + 3 = 23
        Assert.Equal((short)99, frame.Data.Span[23]);
    }

    [Fact]
    public void Retain_keeps_the_buffer_until_the_last_owner_disposes()
    {
        AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 1, 16);
        Assert.Equal(1, frame.RefCount);

        frame.Retain();
        Assert.Equal(2, frame.RefCount);

        frame.Dispose();
        Assert.Equal(1, frame.RefCount);
        Assert.Equal(16, frame.Data.Length);

        frame.Dispose();
        Assert.Equal(0, frame.RefCount);
    }

    [Fact]
    public void Using_a_frame_after_its_last_dispose_throws()
    {
        AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 1, 16);
        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = frame.Data; });
        Assert.Throws<ObjectDisposedException>(() => frame.Retain());
        Assert.Throws<ObjectDisposedException>(() => { _ = frame.Span(0, 0).Length; });
    }

    [Fact]
    public void Index_out_of_range_throws()
    {
        using AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 2, 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = frame.Span(1, 0).Length; });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = frame.Span(0, 2).Length; });
    }

    [Fact]
    public void A_frame_too_large_for_one_buffer_is_refused_with_a_clear_error()
    {
        // 1024 x 1024 x 4096 samples is over 4 billion, more than one array can hold.
        var ex = Assert.Throws<NotSupportedException>(() => AcquisitionFrame.Rent(FrameKind.Fmc, 1024, 1024, 4096));
        Assert.Contains("chunked", ex.Message);
    }

    [Fact]
    public void Invalid_shapes_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AcquisitionFrame.Rent(FrameKind.AScan, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AcquisitionFrame.Rent(FrameKind.AScan, 1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AcquisitionFrame.Rent(FrameKind.AScan, 1, 1, 0));
    }
}

public class AcquisitionParametersTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        Assert.Empty(new AcquisitionParameters().Validate());
    }

    [Fact]
    public void Revise_bumps_the_version_and_leaves_the_original_untouched()
    {
        var original = new AcquisitionParameters { Version = 3 };
        AcquisitionParameters revised = original.Revise(p => p with { GainDb = 12 });

        Assert.Equal(4, revised.Version);
        Assert.Equal(12, revised.GainDb);
        Assert.Equal(3, original.Version);
        Assert.Equal(0, original.GainDb);
    }

    [Fact]
    public void Revise_ignores_a_version_set_inside_the_edit()
    {
        var original = new AcquisitionParameters { Version = 3 };
        AcquisitionParameters revised = original.Revise(p => p with { Version = 100 });
        Assert.Equal(4, revised.Version);
    }

    [Fact]
    public void Validation_reports_each_problem()
    {
        Assert.NotEmpty(new AcquisitionParameters { SamplesPerRecord = 0 }.Validate());
        Assert.NotEmpty(new AcquisitionParameters { SampleRateHz = 0 }.Validate());
        Assert.NotEmpty(new AcquisitionParameters { SoundSpeedMps = -1 }.Validate());
        Assert.Contains(new AcquisitionParameters { ActiveReceiveChannels = 0 }.Validate(), s => s.Contains("active receive channels"));

        // 500 samples at 50 MHz is a 10 microsecond record, so PRF cannot exceed 100 kHz.
        Assert.Contains(new AcquisitionParameters { PrfHz = 200_000 }.Validate(), s => s.Contains("PRF"));

        // The gate must lie inside the recorded window.
        var gateOutside = new AcquisitionParameters { Gate = new GateConfig(2e-6, 20e-6, 0.2) };
        Assert.Contains(gateOutside.Validate(), s => s.Contains("gate"));

        var gateReversed = new AcquisitionParameters { Gate = new GateConfig(5e-6, 3e-6, 0.2) };
        Assert.NotEmpty(gateReversed.Validate());

        var badThreshold = new AcquisitionParameters { Gate = new GateConfig(2e-6, 5e-6, 0) };
        Assert.Contains(badThreshold.Validate(), s => s.Contains("threshold"));
    }

    [Fact]
    public void Device_limits_are_checked_separately()
    {
        var caps = new DeviceCapabilities
        {
            SamplesPerRecord = new IntRange(16, 1024),
            MaxSampleRateHz = 40e6,
            MaxPrfHz = 500,
        };

        Assert.Empty(new AcquisitionParameters { SampleRateHz = 40e6, PrfHz = 500, SamplesPerRecord = 400, Gate = new GateConfig(2e-6, 8e-6, 0.2) }.ValidateAgainst(caps));
        Assert.Contains(new AcquisitionParameters { SamplesPerRecord = 2000, SampleRateHz = 40e6, Gate = new GateConfig(2e-6, 8e-6, 0.2) }.ValidateAgainst(caps), s => s.Contains("samples per record"));
        Assert.Contains(new AcquisitionParameters { SampleRateHz = 50e6 }.ValidateAgainst(caps), s => s.Contains("sample rate"));
        Assert.Contains(new AcquisitionParameters { PrfHz = 1000 }.ValidateAgainst(caps), s => s.Contains("PRF"));
        Assert.Contains(new AcquisitionParameters { ActiveReceiveChannels = 2 }.ValidateAgainst(caps), s => s.Contains("active receive channels"));
    }

    [Fact]
    public void Active_receive_channels_default_to_one_and_are_kept_by_revision()
    {
        var parameters = new AcquisitionParameters();
        Assert.Equal(1, parameters.ActiveReceiveChannels);

        AcquisitionParameters revised = parameters.Revise(p => p with { GainDb = 6 });
        Assert.Equal(1, revised.ActiveReceiveChannels);

        AcquisitionParameters changed = parameters.Revise(p => p with { ActiveReceiveChannels = 4 });
        Assert.Equal(4, changed.ActiveReceiveChannels);
    }
}

public class AdcFormatTests
{
    [Fact]
    public void The_default_is_signed_16_bit_right_aligned()
    {
        AdcFormat adc = AdcFormat.Default;

        Assert.Equal(16, adc.Bits);
        Assert.True(adc.Signed);
        Assert.True(adc.RightAligned);
        Assert.Equal(Math.Pow(2, 15), adc.FullScaleCounts);
        Assert.Equal(-Math.Pow(2, 15), adc.MinCounts);
        Assert.Equal(Math.Pow(2, 15) - 1, adc.MaxCounts);
        Assert.Empty(adc.Validate());
    }

    [Fact]
    public void A_twelve_bit_adc_has_the_expected_full_scale()
    {
        var adc = new AdcFormat { Bits = 12 };

        Assert.Equal(2048.0, adc.FullScaleCounts);
        Assert.Equal(-2048.0, adc.MinCounts);
        Assert.Equal(2047.0, adc.MaxCounts);
        Assert.Empty(adc.Validate());
    }

    [Fact]
    public void Unsupported_formats_are_reported()
    {
        Assert.NotEmpty(new AdcFormat { Bits = 17 }.Validate());
        Assert.NotEmpty(new AdcFormat { Signed = false }.Validate());
        Assert.NotEmpty(new AdcFormat { RightAligned = false }.Validate());
    }
}

public class FrameHeaderTests
{
    [Fact]
    public void Rented_frames_default_to_16_bit_adc_and_no_flags()
    {
        using AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 1, 16);

        Assert.Equal(AdcFormat.Default, frame.Adc);
        Assert.Equal(FrameFlags.None, frame.Flags);

        frame.Adc = new AdcFormat { Bits = 12 };
        frame.Flags = FrameFlags.DeviceOverflow | FrameFlags.Truncated;

        Assert.Equal(12, frame.Adc.Bits);
        Assert.True(frame.Flags.HasFlag(FrameFlags.DeviceOverflow));
        Assert.True(frame.Flags.HasFlag(FrameFlags.Truncated));
    }
}

public class DisplayNeedsTests
{
    [Fact]
    public void Defaults_keep_the_all_channel_behaviour()
    {
        DisplayNeeds needs = DisplayNeeds.Default;

        Assert.Equal(DisplayChannelSelection.All, needs.Channels);
        Assert.Null(needs.MaxPointsAcross);
        Assert.Null(needs.MaxRefreshHz);
        Assert.Empty(needs.Validate());
    }

    [Fact]
    public void Explicit_channels_and_limits_are_validated()
    {
        Assert.Empty(new DisplayNeeds
        {
            Channels = DisplayChannelSelection.Explicit,
            ExplicitChannels = new[] { 0, 2 },
            MaxPointsAcross = 128,
            MaxRefreshHz = 30,
        }.Validate());

        Assert.NotEmpty(new DisplayNeeds { Channels = DisplayChannelSelection.Explicit }.Validate());
        Assert.NotEmpty(new DisplayNeeds { Channels = DisplayChannelSelection.Explicit, ExplicitChannels = new[] { -1 } }.Validate());
        Assert.NotEmpty(new DisplayNeeds { Channels = DisplayChannelSelection.Explicit, ExplicitChannels = new[] { 0, 0 } }.Validate());
        Assert.NotEmpty(new DisplayNeeds { MaxPointsAcross = 0 }.Validate());
        Assert.NotEmpty(new DisplayNeeds { MaxRefreshHz = 0 }.Validate());
    }
}

public class ProbeModelTests
{
    [Fact]
    public void Single_element_probe_sits_at_the_origin()
    {
        ProbeModel probe = ProbeModel.Single();
        Assert.Equal(1, probe.ElementCount);
        Assert.Equal(Vector3.Zero, probe.Positions[0]);
        Assert.Equal(ProbeTopologies.Single, probe.Topology);
    }

    [Fact]
    public void Linear_array_is_centred_with_the_requested_pitch()
    {
        ProbeModel probe = ProbeModel.Linear(128, 0.6e-3);
        Assert.Equal(128, probe.ElementCount);
        Assert.Equal(ProbeTopologies.Linear, probe.Topology);

        double sum = 0;
        foreach (Vector3 position in probe.Positions)
        {
            sum += position.X;
        }

        Assert.InRange(sum, -1e-4, 1e-4);
        Assert.Equal(0.6e-3, probe.Positions[1].X - probe.Positions[0].X, 6);
        Assert.Equal(-(127 / 2.0) * 0.6e-3, probe.Positions[0].X, 6);
    }

    [Fact]
    public void Matrix_array_scales_past_a_thousand_elements()
    {
        ProbeModel probe = ProbeModel.Matrix(32, 32, 0.5e-3, 0.5e-3);
        Assert.Equal(1024, probe.ElementCount);
        Assert.Equal(ProbeTopologies.Matrix, probe.Topology);
        Assert.NotEqual(probe.Positions[0].Y, probe.Positions[1024 - 1].Y);
    }

    [Fact]
    public void Invalid_probe_arguments_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbeModel.Linear(0, 1e-3));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbeModel.Linear(4, 0));
        Assert.Throws<ArgumentException>(() => new ProbeModel(ProbeTopologies.Linear, new[] { Vector3.Zero }, Array.Empty<Vector3>()));
    }
}

public class DeviceOpenOptionsTests
{
    [Fact]
    public void Settings_are_parsed_with_fallbacks()
    {
        var options = new DeviceOpenOptions
        {
            Settings = new Dictionary<string, string> { ["channels"] = "8", ["paced"] = "false" },
        };

        Assert.Equal(8, options.GetInt("channels", 1));
        Assert.Equal(5, options.GetInt("missing", 5));
        Assert.False(options.GetBool("paced", true));
        Assert.True(options.GetBool("missing", true));
    }

    [Fact]
    public void Malformed_settings_are_reported()
    {
        var options = new DeviceOpenOptions
        {
            Settings = new Dictionary<string, string> { ["channels"] = "many", ["paced"] = "maybe" },
        };

        Assert.Throws<ArgumentException>(() => options.GetInt("channels", 1));
        Assert.Throws<ArgumentException>(() => options.GetBool("paced", true));
    }
}
