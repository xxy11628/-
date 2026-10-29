using UltrasoundHost.Contracts;

namespace UltrasoundHost.Plugins.Device.Simulator;

/// <summary>
/// Describes the simulated specimen and how the simulated board behaves. Each receive channel is an
/// independent pulse-echo A-scan: main bang, a defect echo (a bit deeper on every channel) and a back-wall echo.
/// </summary>
public sealed record SimulatorOptions
{
    /// <summary>Number of parallel receive channels (and elements) the board reports.</summary>
    public int ReceiveChannels { get; init; } = 1;

    public int Seed { get; init; } = 1;

    /// <summary>If true frames are emitted at min(PRF, MaxFrameRateHz); if false as fast as the consumer takes them.</summary>
    public bool Paced { get; init; } = true;

    /// <summary>Cap on the emission rate. Operating-system timers cannot pace much above a few hundred Hz.</summary>
    public double MaxFrameRateHz { get; init; } = 200;

    /// <summary>Stop producing after this many frames. 0 = unlimited.</summary>
    public long MaxFrames { get; init; }

    public double CenterFrequencyHz { get; init; } = 5e6;

    public double ThicknessMeters { get; init; } = 0.020;

    public double DefectDepthMeters { get; init; } = 0.010;

    /// <summary>Extra defect depth per channel, so channels differ.</summary>
    public double DefectDepthStepMeters { get; init; } = 0.0005;

    /// <summary>Echo amplitudes in 16-bit ADC counts at 0 dB gain; they are rescaled to the configured ADC depth.</summary>
    public double DefectAmplitude { get; init; } = 9000;

    public double BackwallAmplitude { get; init; } = 16000;

    public double NoiseRmsCounts { get; init; } = 15;

    /// <summary>ADC bit depth the simulated board reports and produces. 16 by default; 12/14 for tests.</summary>
    public int AdcBits { get; init; } = 16;

    /// <summary>Test-only: once this many frames were emitted, jump the sequence number. -1 disables.</summary>
    public long SequenceGapAtFrame { get; init; } = -1;

    /// <summary>Test-only: size of the sequence jump applied at <see cref="SequenceGapAtFrame"/>.</summary>
    public long SequenceGapSize { get; init; } = 1;

    /// <summary>Test-only: set <see cref="FrameFlags.DeviceOverflow"/> on every Nth emitted frame (0 disables).</summary>
    public long InjectDeviceOverflowEvery { get; init; }

    /// <summary>Test-only: emit frames whose <see cref="AcquisitionFrame.ParamVersion"/> is unknown to the session.</summary>
    public bool EmitUnknownParamVersion { get; init; }

    /// <summary>Test-only: delay each frame write by this many milliseconds.</summary>
    public int WriteDelayMilliseconds { get; init; }

    /// <summary>Test-only: throw after this many emitted frames (0 disables).</summary>
    public long ThrowAfterFrames { get; init; }

    /// <summary>The ADC format this options record describes.</summary>
    public AdcFormat Adc => new() { Bits = AdcBits };

    public static SimulatorOptions FromSettings(DeviceOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = new SimulatorOptions
        {
            ReceiveChannels = options.GetInt("channels", 1),
            Seed = options.GetInt("seed", 1),
            Paced = options.GetBool("paced", true),
            MaxFrames = options.GetInt("maxFrames", 0),
            AdcBits = options.GetInt("adcBits", 16),
            SequenceGapAtFrame = options.GetInt("sequenceGapAt", -1),
            SequenceGapSize = options.GetInt("sequenceGapSize", 1),
            InjectDeviceOverflowEvery = options.GetInt("deviceOverflowEvery", 0),
            EmitUnknownParamVersion = options.GetBool("unknownParamVersion", false),
            WriteDelayMilliseconds = options.GetInt("writeDelayMs", 0),
            ThrowAfterFrames = options.GetInt("throwAfterFrames", 0),
        };
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (ReceiveChannels < 1 || ReceiveChannels > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(ReceiveChannels), "the simulator supports 1..4096 channels");
        }

        if (!(CenterFrequencyHz > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(CenterFrequencyHz));
        }

        IReadOnlyList<string> adcProblems = Adc.Validate();
        if (adcProblems.Count > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AdcBits), string.Join("; ", adcProblems));
        }

        if (SequenceGapSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SequenceGapSize));
        }

        if (WriteDelayMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WriteDelayMilliseconds));
        }
    }
}
