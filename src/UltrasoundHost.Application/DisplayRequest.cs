using UltrasoundHost.Contracts;

namespace UltrasoundHost.Application;

/// <summary>
/// What the display path should copy, built as the union of the active views' <see cref="DisplayNeeds"/>
/// (05 §7). The display consumer reads this instead of copying every channel of every frame.
/// </summary>
public sealed record DisplayRequest
{
    public static DisplayRequest Default { get; } = new();

    public DisplayChannelSelection Channels { get; init; } = DisplayChannelSelection.All;

    /// <summary>Channels when <see cref="Channels"/> is Explicit; may also include the selected channel.</summary>
    public IReadOnlyList<int> ExplicitChannels { get; init; } = Array.Empty<int>();

    public int SelectedChannel { get; init; }

    /// <summary>Maximum points per channel, or null for no decimation.</summary>
    public int? MaxPointsAcross { get; init; }

    /// <summary>Maximum snapshot refresh rate, or null for unlimited.</summary>
    public double? MaxRefreshHz { get; init; }

    /// <summary>Resolves the request against the channel count a frame actually has, in ascending order.</summary>
    public IReadOnlyList<int> ResolveChannels(int channelCount)
    {
        if (channelCount < 1)
        {
            return Array.Empty<int>();
        }

        switch (Channels)
        {
            case DisplayChannelSelection.Explicit:
                int[] explicitChannels = ExplicitChannels
                    .Where(channel => channel >= 0 && channel < channelCount)
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray();
                return explicitChannels.Length > 0 ? explicitChannels : new[] { 0 };

            case DisplayChannelSelection.Selected:
                return new[] { Math.Clamp(SelectedChannel, 0, channelCount - 1) };

            default:
                var all = new int[channelCount];
                for (int channel = 0; channel < channelCount; channel++)
                {
                    all[channel] = channel;
                }

                return all;
        }
    }

    /// <summary>
    /// Union of the views' display needs. Channels: All wins, otherwise the union of explicit and
    /// selected channels. Point/refresh limits: the most demanding (smallest) requested value wins;
    /// null means "no constraint" both for the view and for the union.
    /// </summary>
    public static DisplayRequest Create(IEnumerable<ViewDescriptor> views, int selectedChannel)
    {
        ArgumentNullException.ThrowIfNull(views);
        ViewDescriptor[] descriptors = views.ToArray();
        if (descriptors.Length == 0)
        {
            return new DisplayRequest { SelectedChannel = selectedChannel };
        }

        bool anyAll = descriptors.Any(view => view.Needs.Channels == DisplayChannelSelection.All);
        bool anySelected = descriptors.Any(view => view.Needs.Channels == DisplayChannelSelection.Selected);
        int[] explicitChannels = descriptors
            .Where(view => view.Needs.Channels == DisplayChannelSelection.Explicit)
            .SelectMany(view => view.Needs.ExplicitChannels)
            .Distinct()
            .OrderBy(channel => channel)
            .ToArray();

        DisplayChannelSelection mode;
        IReadOnlyList<int> explicitList = Array.Empty<int>();
        if (anyAll)
        {
            mode = DisplayChannelSelection.All;
        }
        else if (explicitChannels.Length > 0)
        {
            mode = DisplayChannelSelection.Explicit;
            var union = new List<int>(explicitChannels);
            if (anySelected && !union.Contains(selectedChannel))
            {
                union.Add(selectedChannel);
            }

            union.Sort();
            explicitList = union;
        }
        else if (anySelected)
        {
            mode = DisplayChannelSelection.Selected;
        }
        else
        {
            mode = DisplayChannelSelection.All;
        }

        int[] pointLimits = descriptors
            .Select(view => view.Needs.MaxPointsAcross)
            .Where(limit => limit.HasValue)
            .Select(limit => limit!.Value)
            .ToArray();
        double[] rateLimits = descriptors
            .Select(view => view.Needs.MaxRefreshHz)
            .Where(limit => limit.HasValue)
            .Select(limit => limit!.Value)
            .ToArray();

        return new DisplayRequest
        {
            Channels = mode,
            ExplicitChannels = explicitList,
            SelectedChannel = selectedChannel,
            MaxPointsAcross = pointLimits.Length == 0 ? null : pointLimits.Min(),
            MaxRefreshHz = rateLimits.Length == 0 ? null : rateLimits.Min(),
        };
    }
}
