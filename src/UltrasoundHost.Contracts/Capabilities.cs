using System.Globalization;

namespace UltrasoundHost.Contracts;

/// <summary>Inclusive integer range. Capability declarations use ranges, never hard-coded numbers.</summary>
public readonly record struct IntRange(int Min, int Max)
{
    public static IntRange Any => new(0, int.MaxValue);

    public static IntRange Exactly(int value) => new(value, value);

    public bool IsValid => Min <= Max;

    public bool Contains(int value) => value >= Min && value <= Max;

    public bool Overlaps(IntRange other) => Min <= other.Max && other.Min <= Max;

    public override string ToString()
    {
        string max = Max == int.MaxValue ? "inf" : Max.ToString(CultureInfo.InvariantCulture);
        return "[" + Min.ToString(CultureInfo.InvariantCulture) + ".." + max + "]";
    }
}

/// <summary>Ways a device can deliver data. A requirement lists acceptable ones (any-of).</summary>
[Flags]
public enum AcquisitionSchemes
{
    None = 0,
    AScan = 1,
    Fmc = 2,
    Hmc = 4,
    Sparse = 8,
    SubAperture = 16,
    PlaneWave = 32,
    HwBeamformed = 64,
}

/// <summary>Probe geometry families. A requirement lists acceptable ones (any-of).</summary>
[Flags]
public enum ProbeTopologies
{
    None = 0,
    Single = 1,
    Linear = 2,
    Matrix = 4,
    Annular = 8,
    Curved = 16,
}

/// <summary>What a plugin needs from the machine it runs on. Checked when plugins are loaded.</summary>
public sealed record HostRequirements
{
    public static HostRequirements None { get; } = new();

    public long GpuMemoryBytes { get; init; }

    /// <summary>Feature tags that must all be present, for example "gpu.cuda".</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

/// <summary>What the machine offers. Supplied by the application when it creates the host.</summary>
public sealed record HostCapabilities
{
    public long GpuMemoryBytes { get; init; }

    public IReadOnlyCollection<string> Tags { get; init; } = Array.Empty<string>();
}

/// <summary>
/// What a plugin needs from the connected device. Checked per session, before acquisition starts.
/// Defaults mean "don't care".
/// </summary>
public sealed record DeviceRequirements
{
    public static DeviceRequirements None { get; } = new();

    public IntRange ElementCount { get; init; } = IntRange.Any;

    public IntRange ReceiveChannels { get; init; } = IntRange.Any;

    /// <summary>Acceptable schemes, any-of. <see cref="AcquisitionSchemes.None"/> = don't care.</summary>
    public AcquisitionSchemes Schemes { get; init; } = AcquisitionSchemes.None;

    /// <summary>Acceptable topologies, any-of. <see cref="ProbeTopologies.None"/> = don't care.</summary>
    public ProbeTopologies Topologies { get; init; } = ProbeTopologies.None;

    public double MinThroughputMBps { get; init; }

    public bool NeedsMultiBoardSync { get; init; }
}

/// <summary>What a device offers. Reported by the device when it connects.</summary>
public sealed record DeviceCapabilities
{
    public string Model { get; init; } = string.Empty;

    public IntRange ElementCount { get; init; } = new(1, 1);

    /// <summary>Parallel receive channels. Can be smaller than the element count when a multiplexer is used.</summary>
    public IntRange ReceiveChannels { get; init; } = new(1, 1);

    public AcquisitionSchemes Schemes { get; init; } = AcquisitionSchemes.AScan;

    public ProbeTopologies Topologies { get; init; } = ProbeTopologies.Single;

    public double ThroughputMBps { get; init; }

    public bool SupportsMultiBoardSync { get; init; }

    public IntRange SamplesPerRecord { get; init; } = new(1, int.MaxValue);

    /// <summary>How the device's samples map to a fraction of full scale. Defaults to signed 16-bit.</summary>
    public AdcFormat Adc { get; init; } = AdcFormat.Default;

    public double MaxSampleRateHz { get; init; } = double.PositiveInfinity;

    public double MaxPrfHz { get; init; } = double.PositiveInfinity;
}
