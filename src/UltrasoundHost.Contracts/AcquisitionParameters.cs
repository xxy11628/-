namespace UltrasoundHost.Contracts;

/// <summary>Amplitude gate in absolute time (from the pulse). Threshold is a fraction of full scale.</summary>
public sealed record GateConfig(double StartSeconds, double EndSeconds, double Threshold)
{
    public static GateConfig Default { get; } = new(2.0e-6, 5.5e-6, 0.2);
}

/// <summary>
/// Immutable acquisition settings. Every change produces a new instance with a higher
/// <see cref="Version"/> (see <see cref="Revise"/>); frames carry the version they were acquired
/// under so consumers can drop frames that no longer match.
/// </summary>
public sealed record AcquisitionParameters
{
    public int Version { get; init; }

    public double SampleRateHz { get; init; } = 50e6;

    public int SamplesPerRecord { get; init; } = 500;

    public double PrfHz { get; init; } = 1000;

    public double GainDb { get; init; }

    public double SoundSpeedMps { get; init; } = 5900;

    public double StartDelaySeconds { get; init; }

    public GateConfig Gate { get; init; } = GateConfig.Default;

    /// <summary>
    /// Number of receive channels the acquisition actually uses. Must be inside the device's
    /// <see cref="DeviceCapabilities.ReceiveChannels"/> range; the shell and session read the actual
    /// value from here instead of guessing from the capability range (06 §C5).
    /// </summary>
    public int ActiveReceiveChannels { get; init; } = 1;

    /// <summary>Applies <paramref name="edit"/> and bumps the version by one.</summary>
    public AcquisitionParameters Revise(Func<AcquisitionParameters, AcquisitionParameters> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return edit(this) with { Version = Version + 1 };
    }

    /// <summary>Device-independent sanity checks. Empty list means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!(SampleRateHz > 0))
        {
            problems.Add("sample rate must be > 0");
        }

        if (SamplesPerRecord < 1)
        {
            problems.Add("samples per record must be >= 1");
        }

        if (!(PrfHz > 0))
        {
            problems.Add("PRF must be > 0");
        }

        if (!(SoundSpeedMps > 0))
        {
            problems.Add("sound speed must be > 0");
        }

        if (!(StartDelaySeconds >= 0))
        {
            problems.Add("start delay must be >= 0");
        }

        if (ActiveReceiveChannels < 1)
        {
            problems.Add("active receive channels must be >= 1");
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        double recordSeconds = SamplesPerRecord / SampleRateHz;
        double cycleSeconds = StartDelaySeconds + recordSeconds;
        if (PrfHz > 1.0 / cycleSeconds)
        {
            problems.Add("PRF " + PrfHz.ToString("G6") + " Hz is higher than 1 / record duration ("
                + (1.0 / cycleSeconds).ToString("G6") + " Hz)");
        }

        if (!(Gate.EndSeconds > Gate.StartSeconds))
        {
            problems.Add("gate end must be after gate start");
        }

        if (Gate.StartSeconds < StartDelaySeconds || Gate.EndSeconds > cycleSeconds)
        {
            problems.Add("gate must lie inside the recorded time window");
        }

        if (!(Gate.Threshold > 0 && Gate.Threshold <= 1))
        {
            problems.Add("gate threshold must be in (0, 1]");
        }

        return problems;
    }

    /// <summary>Sanity checks plus the limits a specific device reported.</summary>
    public IReadOnlyList<string> ValidateAgainst(DeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var problems = new List<string>(Validate());
        if (!capabilities.SamplesPerRecord.Contains(SamplesPerRecord))
        {
            problems.Add("samples per record " + SamplesPerRecord + " is outside the device range " + capabilities.SamplesPerRecord);
        }

        if (!capabilities.ReceiveChannels.Contains(ActiveReceiveChannels))
        {
            problems.Add("active receive channels " + ActiveReceiveChannels + " is outside the device range " + capabilities.ReceiveChannels);
        }

        if (SampleRateHz > capabilities.MaxSampleRateHz)
        {
            problems.Add("sample rate " + SampleRateHz.ToString("G6") + " Hz exceeds the device maximum " + capabilities.MaxSampleRateHz.ToString("G6") + " Hz");
        }

        if (PrfHz > capabilities.MaxPrfHz)
        {
            problems.Add("PRF " + PrfHz.ToString("G6") + " Hz exceeds the device maximum " + capabilities.MaxPrfHz.ToString("G6") + " Hz");
        }

        return problems;
    }
}
