using Microsoft.Extensions.DependencyInjection;
using UltrasoundHost.Application;
using UltrasoundHost.Contracts;
using UltrasoundHost.Plugins.Device.Simulator;
using UltrasoundHost.Plugins.Processing.Basic;

namespace UltrasoundHost.Tests;

/// <summary>WP5 / DP8: display request union, selective copy and peak-preserving decimation (05 §7).</summary>
public class DisplayRequestTests
{
    private static ViewDescriptor View(string id, DisplayNeeds needs) =>
        new ViewDescriptor(id, id, "Custom") { Needs = needs };

    [Fact]
    public void The_union_prefers_all_channels_and_the_most_demanding_limits()
    {
        var views = new[]
        {
            View("selected", new DisplayNeeds { Channels = DisplayChannelSelection.Selected, MaxRefreshHz = 30 }),
            View("explicit", new DisplayNeeds
            {
                Channels = DisplayChannelSelection.Explicit,
                ExplicitChannels = new[] { 0, 3 },
                MaxPointsAcross = 128,
                MaxRefreshHz = 15,
            }),
            new ViewDescriptor("any", "any", "Custom"),
        };

        DisplayRequest request = DisplayRequest.Create(views, selectedChannel: 5);

        Assert.Equal(DisplayChannelSelection.All, request.Channels);
        Assert.Equal(128, request.MaxPointsAcross);
        Assert.Equal(15, request.MaxRefreshHz);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, request.ResolveChannels(6));
    }

    [Fact]
    public void Selected_and_explicit_views_are_merged_when_no_view_needs_all_channels()
    {
        var views = new[]
        {
            View("selected", new DisplayNeeds { Channels = DisplayChannelSelection.Selected }),
            View("explicit", new DisplayNeeds
            {
                Channels = DisplayChannelSelection.Explicit,
                ExplicitChannels = new[] { 0, 3 },
            }),
        };

        DisplayRequest request = DisplayRequest.Create(views, selectedChannel: 5);

        Assert.Equal(DisplayChannelSelection.Explicit, request.Channels);
        Assert.Equal(new[] { 0, 3, 5 }, request.ExplicitChannels);
        Assert.Equal(new[] { 0, 3, 5 }, request.ResolveChannels(8));
        Assert.Equal(new[] { 0, 3 }, request.ResolveChannels(4));
    }

    [Fact]
    public void Capture_copies_only_requested_channels_and_preserves_peaks()
    {
        using AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 4, 100);
        frame.SampleRateHz = 50e6;
        frame.ParamVersion = 1;
        frame.Span(0, 0).Clear();
        frame.Span(0, 1).Clear();
        frame.Span(0, 2).Clear();
        frame.Span(0, 3).Clear();
        frame.Span(0, 0)[50] = 12345;
        frame.Span(0, 2)[70] = -11000;

        var parameters = new AcquisitionParameters { Version = 1 };
        var request = new DisplayRequest
        {
            Channels = DisplayChannelSelection.Explicit,
            ExplicitChannels = new[] { 0, 2 },
            MaxPointsAcross = 16,
        };

        FrameSnapshot snapshot = FrameSnapshot.Capture(frame, parameters, request);

        Assert.Equal(2, snapshot.ChannelCount);
        Assert.Equal(new[] { 0, 2 }, snapshot.ChannelNumbers);
        Assert.All(snapshot.Channels, samples => Assert.InRange(samples.Length, 2, 16));

        int peakIndex = Array.IndexOf(snapshot.Channels[0], (short)12345);
        Assert.True(peakIndex >= 0);
        Assert.Equal(50 / 50e6, snapshot.SampleTimeSeconds(0, peakIndex), 12);
        Assert.Contains((short)-11000, snapshot.Channels[1]);
    }

    [Fact]
    public void The_default_request_copies_every_channel_without_decimation()
    {
        using AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 3, 80);
        frame.SampleRateHz = 50e6;
        frame.ParamVersion = 1;
        var parameters = new AcquisitionParameters { Version = 1 };

        FrameSnapshot snapshot = FrameSnapshot.Capture(frame, parameters, DisplayRequest.Default);

        Assert.Equal(3, snapshot.ChannelCount);
        Assert.Equal(new[] { 0, 1, 2 }, snapshot.ChannelNumbers);
        Assert.All(snapshot.Channels, samples => Assert.Equal(80, samples.Length));
    }

    [Fact]
    public async Task A_session_with_a_selective_view_copies_only_what_it_needs()
    {
        var plugins = new IPlugin[]
        {
            new SimulatorPlugin(),
            new GatePeakPlugin(),
            new SelectiveViewPlugin(),
        };

        await using var session = new ShellSession(plugins, options: new SessionOptions { Paced = false });
        await session.InitializeAsync();
        await session.OpenDeviceAsync(16);
        await session.StartAcquisitionAsync(new AcquisitionParameters { Version = 1, ActiveReceiveChannels = 16 });
        await SessionWait.UntilAsync(() => session.LatestSnapshot is not null, "a display snapshot");

        FrameSnapshot snapshot = session.LatestSnapshot!;
        await session.StopAcquisitionAsync();

        Assert.Equal(new[] { 0, 5 }, snapshot.ChannelNumbers);
        Assert.Equal(2, snapshot.ChannelCount);
        Assert.All(snapshot.Channels, samples => Assert.InRange(samples.Length, 2, 16));
    }
}

/// <summary>View that wants two explicit channels, decimated to at most 16 points.</summary>
internal sealed class SelectiveViewPlugin : IPlugin, IViewContribution
{
    public const string PluginId = "view.selective";

    public PluginManifest Manifest { get; } = new PluginManifest(PluginId, new Version(0, 1, 0), PluginKind.View, "选择性视图");

    public IReadOnlyList<ViewDescriptor> Views { get; } = new[]
    {
        new ViewDescriptor("selective", "选择性视图", ViewKinds.AScan, AcquisitionSchemes.AScan)
        {
            Needs = new DisplayNeeds
            {
                Channels = DisplayChannelSelection.Explicit,
                ExplicitChannels = new[] { 0, 5 },
                MaxPointsAcross = 16,
            },
        },
    };

    public void Register(IServiceCollection services)
    {
    }
}
