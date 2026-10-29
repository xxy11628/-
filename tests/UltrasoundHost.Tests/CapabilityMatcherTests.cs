using UltrasoundHost.Contracts;
using UltrasoundHost.Core;

namespace UltrasoundHost.Tests;

public class CapabilityMatcherTests
{
    private static readonly DeviceCapabilities SingleChannelBoard = new()
    {
        Model = "single",
        ElementCount = IntRange.Exactly(1),
        ReceiveChannels = IntRange.Exactly(1),
        Schemes = AcquisitionSchemes.AScan,
        Topologies = ProbeTopologies.Single,
        ThroughputMBps = 100,
    };

    private static readonly DeviceCapabilities ArrayBoard = new()
    {
        Model = "array",
        ElementCount = new IntRange(32, 128),
        ReceiveChannels = new IntRange(32, 128),
        Schemes = AcquisitionSchemes.Fmc | AcquisitionSchemes.SubAperture,
        Topologies = ProbeTopologies.Linear | ProbeTopologies.Matrix,
        ThroughputMBps = 1100,
        SupportsMultiBoardSync = true,
    };

    [Fact]
    public void Default_requirements_match_any_device()
    {
        Assert.Empty(CapabilityMatcher.Match(DeviceRequirements.None, SingleChannelBoard));
        Assert.Empty(CapabilityMatcher.Match(DeviceRequirements.None, ArrayBoard));
    }

    [Fact]
    public void Ranges_need_to_overlap_not_to_be_equal()
    {
        var needs = new DeviceRequirements { ElementCount = new IntRange(16, 4096) };
        Assert.Empty(CapabilityMatcher.Match(needs, ArrayBoard));

        IReadOnlyList<string> unmet = CapabilityMatcher.Match(needs, SingleChannelBoard);
        Assert.Contains(unmet, s => s.Contains("element count"));
    }

    [Fact]
    public void A_requirement_that_names_128_still_works_when_the_device_offers_1024()
    {
        var bigDevice = new DeviceCapabilities { ElementCount = new IntRange(16, 1024), ReceiveChannels = new IntRange(16, 1024) };
        var needs = new DeviceRequirements { ElementCount = new IntRange(16, 4096), ReceiveChannels = new IntRange(32, 4096) };
        Assert.Empty(CapabilityMatcher.Match(needs, bigDevice));
    }

    [Fact]
    public void Schemes_are_any_of()
    {
        var wantsFmcOrAScan = new DeviceRequirements { Schemes = AcquisitionSchemes.Fmc | AcquisitionSchemes.AScan };
        Assert.Empty(CapabilityMatcher.Match(wantsFmcOrAScan, SingleChannelBoard));
        Assert.Empty(CapabilityMatcher.Match(wantsFmcOrAScan, ArrayBoard));

        var wantsPlaneWave = new DeviceRequirements { Schemes = AcquisitionSchemes.PlaneWave };
        Assert.Contains(CapabilityMatcher.Match(wantsPlaneWave, ArrayBoard), s => s.Contains("scheme"));
    }

    [Fact]
    public void Topologies_are_any_of()
    {
        var wantsMatrix = new DeviceRequirements { Topologies = ProbeTopologies.Matrix };
        Assert.Empty(CapabilityMatcher.Match(wantsMatrix, ArrayBoard));
        Assert.Contains(CapabilityMatcher.Match(wantsMatrix, SingleChannelBoard), s => s.Contains("topology"));
    }

    [Fact]
    public void Throughput_must_reach_the_minimum()
    {
        var needs = new DeviceRequirements { MinThroughputMBps = 500 };
        Assert.Empty(CapabilityMatcher.Match(needs, ArrayBoard));
        Assert.Contains(CapabilityMatcher.Match(needs, SingleChannelBoard), s => s.Contains("throughput"));
    }

    [Fact]
    public void Multi_board_sync_is_only_checked_when_requested()
    {
        var needs = new DeviceRequirements { NeedsMultiBoardSync = true };
        Assert.Empty(CapabilityMatcher.Match(needs, ArrayBoard));
        Assert.Contains(CapabilityMatcher.Match(needs, SingleChannelBoard), s => s.Contains("synchronisation"));
    }

    [Fact]
    public void Every_unmet_reason_is_reported()
    {
        var needs = new DeviceRequirements
        {
            ElementCount = new IntRange(64, 128),
            Schemes = AcquisitionSchemes.Fmc,
            MinThroughputMBps = 1000,
        };

        Assert.Equal(3, CapabilityMatcher.Match(needs, SingleChannelBoard).Count);
    }
}
