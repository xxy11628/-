using UltrasoundHost.Contracts;

namespace UltrasoundHost.Core;

/// <summary>Checks what a plugin needs against what a device offers.</summary>
public static class CapabilityMatcher
{
    /// <summary>
    /// Returns the reasons the requirement is not met; an empty list means it is met.
    /// Ranges must overlap, scheme and topology sets are any-of (None = don't care),
    /// throughput must be at least the requested minimum, multi-board sync must exist if requested.
    /// </summary>
    public static IReadOnlyList<string> Match(DeviceRequirements need, DeviceCapabilities have)
    {
        ArgumentNullException.ThrowIfNull(need);
        ArgumentNullException.ThrowIfNull(have);
        var unmet = new List<string>();

        if (!need.ElementCount.Overlaps(have.ElementCount))
        {
            unmet.Add("element count: need " + need.ElementCount + ", device offers " + have.ElementCount);
        }

        if (!need.ReceiveChannels.Overlaps(have.ReceiveChannels))
        {
            unmet.Add("receive channels: need " + need.ReceiveChannels + ", device offers " + have.ReceiveChannels);
        }

        if (need.Schemes != AcquisitionSchemes.None && (need.Schemes & have.Schemes) == AcquisitionSchemes.None)
        {
            unmet.Add("acquisition scheme: need any of " + need.Schemes + ", device offers " + have.Schemes);
        }

        if (need.Topologies != ProbeTopologies.None && (need.Topologies & have.Topologies) == ProbeTopologies.None)
        {
            unmet.Add("probe topology: need any of " + need.Topologies + ", device offers " + have.Topologies);
        }

        if (have.ThroughputMBps < need.MinThroughputMBps)
        {
            unmet.Add("throughput: need at least " + need.MinThroughputMBps.ToString("G6") + " MB/s, device offers " + have.ThroughputMBps.ToString("G6") + " MB/s");
        }

        if (need.NeedsMultiBoardSync && !have.SupportsMultiBoardSync)
        {
            unmet.Add("multi-board synchronisation is required but the device does not support it");
        }

        return unmet;
    }
}
