namespace UltrasoundHost.Contracts;

/// <summary>
/// Kinds the shell knows how to render. A kind is still a free-form string on the descriptor:
/// a plugin may declare a kind the installed shell does not know (the shell shows a placeholder),
/// and a newer shell may know more kinds. These constants only stop the built-in names drifting.
/// </summary>
public static class ViewKinds
{
    /// <summary>Amplitude samples over time for one or more receive channels.</summary>
    public const string AScan = "AScan";

    /// <summary>Tabular gate/peak results of the most recent frame.</summary>
    public const string GateTable = "GateTable";
}

/// <summary>Which receive channels a view needs. Used by the display path to copy only what is shown (05 §7).</summary>
public enum DisplayChannelSelection
{
    All,
    Selected,
    Explicit,
}

/// <summary>
/// What a view needs from the display path. Defaults equal the pre-M2 behaviour: every channel,
/// no decimation and no refresh limit, so existing views keep working unchanged.
/// </summary>
public sealed record DisplayNeeds
{
    public static DisplayNeeds Default { get; } = new();

    public DisplayChannelSelection Channels { get; init; } = DisplayChannelSelection.All;

    /// <summary>Channel indexes when <see cref="Channels"/> is Explicit; ignored otherwise.</summary>
    public IReadOnlyList<int> ExplicitChannels { get; init; } = Array.Empty<int>();

    /// <summary>Maximum points along the horizontal axis, or null for no decimation.</summary>
    public int? MaxPointsAcross { get; init; }

    /// <summary>Maximum snapshot refresh rate, or null for unlimited.</summary>
    public double? MaxRefreshHz { get; init; }

    /// <summary>Problems with these needs; an empty list means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Channels == DisplayChannelSelection.Explicit)
        {
            if (ExplicitChannels.Count == 0)
            {
                problems.Add("explicit channel selection needs at least one channel");
            }

            if (ExplicitChannels.Any(channel => channel < 0))
            {
                problems.Add("channel indexes must be >= 0");
            }

            if (ExplicitChannels.Distinct().Count() != ExplicitChannels.Count)
            {
                problems.Add("channel indexes must be unique");
            }
        }

        if (MaxPointsAcross is < 1)
        {
            problems.Add("max points across must be >= 1 when set");
        }

        if (MaxRefreshHz is <= 0)
        {
            problems.Add("max refresh rate must be > 0 when set");
        }

        return problems;
    }
}

/// <summary>
/// A view a plugin offers, described without any UI technology. The shell turns descriptors into
/// docked panels; plugins never create windows themselves.
/// </summary>
/// <param name="Id">Unique id, for example "ascan".</param>
/// <param name="Title">Display title.</param>
/// <param name="ViewKind">Free-form kind; see <see cref="ViewKinds"/> for the built-in ones.</param>
/// <param name="Schemes">Acquisition schemes the view can show; None = any.</param>
public sealed record ViewDescriptor(string Id, string Title, string ViewKind, AcquisitionSchemes Schemes = AcquisitionSchemes.None)
{
    /// <summary>What the view needs from the display path; defaults to the all-channel behaviour.</summary>
    public DisplayNeeds Needs { get; init; } = DisplayNeeds.Default;

    /// <summary>
    /// True when the view can show data acquired with <paramref name="deviceSchemes"/>.
    /// Same any-of rule as <see cref="DeviceRequirements.Schemes"/>: None means "any".
    /// </summary>
    public bool Matches(AcquisitionSchemes deviceSchemes) =>
        Schemes == AcquisitionSchemes.None || (Schemes & deviceSchemes) != AcquisitionSchemes.None;
}

public interface IViewContribution
{
    IReadOnlyList<ViewDescriptor> Views { get; }
}
