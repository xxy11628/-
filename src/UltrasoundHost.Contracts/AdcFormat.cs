namespace UltrasoundHost.Contracts;

/// <summary>
/// How the samples carried by <see cref="AcquisitionFrame"/> map back to a fraction of full scale.
/// Samples stay <see cref="short"/> on the wire and in memory; this record says how many bits are
/// meaningful and how they are aligned, so processors never hard-code a full-scale count.
/// </summary>
public sealed record AdcFormat
{
    /// <summary>Default: signed, right-aligned, 16 bits. Old code that does not set a format behaves as before.</summary>
    public static AdcFormat Default { get; } = new();

    /// <summary>Number of ADC bits actually used, 1..16.</summary>
    public int Bits { get; init; } = 16;

    /// <summary>True for two's-complement samples. Only signed formats are supported in this stage.</summary>
    public bool Signed { get; init; } = true;

    /// <summary>True when the ADC value occupies the low bits and the sign is extended. Only right-aligned is supported now.</summary>
    public bool RightAligned { get; init; } = true;

    /// <summary>Counts that correspond to +1.0 of full scale: 2^(Bits-1) for a signed format.</summary>
    public double FullScaleCounts => Math.Pow(2.0, Bits - 1);

    /// <summary>Smallest sample count of this format.</summary>
    public double MinCounts => -FullScaleCounts;

    /// <summary>Largest sample count of this format.</summary>
    public double MaxCounts => FullScaleCounts - 1.0;

    /// <summary>Problems with this format; an empty list means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Bits < 1 || Bits > 16)
        {
            problems.Add("ADC bits must be in 1..16, got " + Bits);
        }

        if (!Signed)
        {
            problems.Add("only signed ADC formats are supported");
        }

        if (!RightAligned)
        {
            problems.Add("only right-aligned ADC formats are supported");
        }

        return problems;
    }
}
