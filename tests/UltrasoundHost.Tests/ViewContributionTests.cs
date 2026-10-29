using Microsoft.Extensions.DependencyInjection;
using UltrasoundHost.Contracts;
using UltrasoundHost.Plugins.Device.Simulator;
using UltrasoundHost.Plugins.Processing.Basic;
using UltrasoundHost.Plugins.View.Basic;

namespace UltrasoundHost.Tests;

public class ViewContributionTests
{
    [Fact]
    public void Descriptors_are_well_formed_and_unique()
    {
        IReadOnlyList<ViewDescriptor> views = new BasicViewPlugin().Views;

        Assert.NotEmpty(views);
        Assert.Equal(views.Count, views.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(views, view =>
        {
            Assert.False(string.IsNullOrWhiteSpace(view.Id));
            Assert.False(string.IsNullOrWhiteSpace(view.Title));
            Assert.False(string.IsNullOrWhiteSpace(view.ViewKind));
            Assert.NotEqual(AcquisitionSchemes.None, view.Schemes);
        });
        Assert.Contains(views, v => v.ViewKind == ViewKinds.AScan);
        Assert.Contains(views, v => v.ViewKind == ViewKinds.GateTable);
    }

    [Fact]
    public void A_descriptor_only_matches_schemes_it_can_show()
    {
        var aScanOnly = new ViewDescriptor("a", "A", ViewKinds.AScan, AcquisitionSchemes.AScan);
        var anyScheme = new ViewDescriptor("b", "B", "Custom");

        Assert.True(aScanOnly.Matches(AcquisitionSchemes.AScan));
        Assert.False(aScanOnly.Matches(AcquisitionSchemes.Fmc));
        Assert.True(aScanOnly.Matches(AcquisitionSchemes.AScan | AcquisitionSchemes.Fmc));
        Assert.True(anyScheme.Matches(AcquisitionSchemes.Fmc));
        Assert.False(aScanOnly.Matches(AcquisitionSchemes.None));
    }

    [Fact]
    public async Task The_view_plugin_is_resolved_from_the_container()
    {
        var plugin = new BasicViewPlugin();
        await using Session session = await Session.StartAsync(new IPlugin[] { plugin });

        IViewContribution contribution = Assert.Single(session.Provider.GetServices<IViewContribution>());
        Assert.Same(plugin, contribution);
        Assert.Equal(plugin.Views.Count, contribution.Views.Count);
    }

    [Fact]
    public async Task The_real_chain_passes_the_session_check_and_exposes_two_views()
    {
        var plugins = new IPlugin[] { new SimulatorPlugin(), new GatePeakPlugin(), new BasicViewPlugin() };
        await using Session session = await Session.StartAsync(plugins);

        IDevice device = await new SimulatorDeviceFactory().OpenAsync(new DeviceOpenOptions());
        await using (device)
        {
            Assert.True(session.Host.CheckSession(device.Capabilities).AllSatisfied);
            Assert.All(
                session.Provider.GetServices<IViewContribution>().SelectMany(v => v.Views),
                view => Assert.True(view.Matches(device.Capabilities.Schemes)));
        }

        Assert.Equal(2, session.Provider.GetServices<IViewContribution>().SelectMany(v => v.Views).Count());
    }
}
