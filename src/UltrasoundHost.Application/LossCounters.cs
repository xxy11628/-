namespace UltrasoundHost.Application;

/// <summary>Where a frame can go. Record is reserved for the M6 storage plugin.</summary>
public enum FramePathKind
{
    Inspection,
    Record,
    Display,
}

/// <summary>Per-path accounting. Every frame offered to a path ends up Processed, Dropped or UnknownVersion.</summary>
public sealed record PathCounters(long Offered, long Processed, long Dropped, long UnknownVersion);

/// <summary>
/// Immutable snapshot of what happened to the frames seen by the session (05 §5). Reading it never
/// blocks and the value is safe to hand to the UI thread.
/// </summary>
public sealed record LossCounters
{
    public static LossCounters Empty { get; } = new();

    /// <summary>Frames the fanout took from the device channel.</summary>
    public long Received { get; init; }

    /// <summary>Frames the device skipped, detected from gaps in <see cref="Contracts.AcquisitionFrame.Sequence"/>.</summary>
    public long SequenceGaps { get; init; }

    /// <summary>Frames that arrived with <see cref="Contracts.FrameFlags.DeviceOverflow"/> set.</summary>
    public long DeviceOverflow { get; init; }

    /// <summary>Duplicate or backwards sequence numbers, treated as a device error.</summary>
    public long SequenceViolations { get; init; }

    public IReadOnlyDictionary<FramePathKind, PathCounters> Paths { get; init; } =
        new Dictionary<FramePathKind, PathCounters>
        {
            [FramePathKind.Inspection] = new(0, 0, 0, 0),
            [FramePathKind.Record] = new(0, 0, 0, 0),
            [FramePathKind.Display] = new(0, 0, 0, 0),
        };

    public PathCounters Inspection => this[FramePathKind.Inspection];

    public PathCounters Record => this[FramePathKind.Record];

    public PathCounters Display => this[FramePathKind.Display];

    public PathCounters this[FramePathKind kind] =>
        Paths.TryGetValue(kind, out PathCounters? counters) ? counters : new(0, 0, 0, 0);

    /// <summary>
    /// Compatibility with the pre-WP4 session metric: frames whose parameter version was not in the
    /// history. Unlike the old StaleDropped it is not the normal result of changing parameters.
    /// </summary>
    public long StaleDropped => Paths.Values.Sum(counters => counters.UnknownVersion);

    /// <summary>
    /// True when the inspection path lost (or could not attribute) at least one frame, so the result
    /// of this session must be treated as incomplete (05 P4). Device-side gaps, overflow and sequence
    /// violations also break completeness even if they never reach a queue.
    /// </summary>
    public bool InspectionIncomplete =>
        Inspection.Dropped + Inspection.UnknownVersion > 0
        || SequenceGaps > 0
        || DeviceOverflow > 0
        || SequenceViolations > 0;
}
