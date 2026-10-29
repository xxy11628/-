using UltrasoundHost.Contracts;

namespace UltrasoundHost.Core;

/// <summary>
/// Rejected = a precondition failed before the plugin ran (version, host capabilities, dependencies).
/// Faulted = the plugin itself threw. Neither affects unrelated plugins.
/// </summary>
public enum PluginState
{
    Discovered,
    Registered,
    Initialized,
    Running,
    Stopped,
    Rejected,
    Faulted,
}

/// <summary>Id is the stable identity; DisplayName comes from the manifest and is meant for the UI.</summary>
public sealed record PluginStatus(string Id, string DisplayName, PluginKind Kind, PluginState State, string? Reason);

public sealed record SessionCheckItem(string PluginId, bool Satisfied, IReadOnlyList<string> Unmet);

/// <summary>Per-plugin outcome of matching device capabilities, produced before acquisition starts.</summary>
public sealed record SessionCheckResult(IReadOnlyList<SessionCheckItem> Items)
{
    public bool AllSatisfied => Items.All(item => item.Satisfied);
}
