namespace UltrasoundHost.Application;

/// <summary>
/// What the session needs to know that is not device-independent acquisition data: which device
/// plugin to open, how to open it, and the queue/history sizes used by the data path.
/// Defaults follow 05 §2: inspection/record queues are an order of magnitude larger than display.
/// </summary>
public sealed record SessionOptions
{
    /// <summary>Device type as reported by an <see cref="Contracts.IDeviceFactory"/>, e.g. "simulator".</summary>
    public string DeviceType { get; init; } = "simulator";

    /// <summary>Extra device settings, passed through as <see cref="Contracts.DeviceOpenOptions.Settings"/>.</summary>
    public IReadOnlyDictionary<string, string> DeviceSettings { get; init; } = new Dictionary<string, string>();

    /// <summary>True asks the device to pace frames at its configured rate instead of as fast as possible.</summary>
    public bool Paced { get; init; } = true;

    /// <summary>Capacity of the device-to-fanout channel.</summary>
    public int SourceQueueCapacity { get; init; } = 8;

    /// <summary>Capacity of the inspection queue. Full means the host cannot keep up; frames are counted, never silently dropped.</summary>
    public int InspectionQueueCapacity { get; init; } = 64;

    /// <summary>Capacity of the record queue (used when a storage plugin lands in M6).</summary>
    public int RecordQueueCapacity { get; init; } = 64;

    /// <summary>Capacity of the display queue. Display may drop the oldest frame, but every drop is counted.</summary>
    public int DisplayQueueCapacity { get; init; } = 8;

    /// <summary>How many parameter versions are kept for per-frame processing (05 §6).</summary>
    public int ParameterHistoryDepth { get; init; } = 16;
}
