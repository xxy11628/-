using Microsoft.Extensions.DependencyInjection;
using UltrasoundHost.Contracts;

namespace UltrasoundHost.Plugins.View.Basic;

/// <summary>
/// View plugin: declares the A-scan waveform and the gate-result table. It contains no UI code:
/// the shell owns rendering and decides what to do with kinds it does not know.
/// </summary>
public sealed class BasicViewPlugin : IPlugin, IViewContribution
{
    public const string PluginId = "view.basic";

    public const string AScanViewId = "ascan";

    public const string GateTableViewId = "gates";

    public PluginManifest Manifest { get; } = new PluginManifest(PluginId, new Version(0, 1, 0), PluginKind.View, "基础 A 扫视图")
    {
        // Both views only make sense for pulse-echo A-scans, so the session check can refuse
        // other device modes before acquisition starts.
        DeviceNeeds = new DeviceRequirements { Schemes = AcquisitionSchemes.AScan },
    };

    public IReadOnlyList<ViewDescriptor> Views { get; } = new[]
    {
        new ViewDescriptor(AScanViewId, "A 扫波形", ViewKinds.AScan, AcquisitionSchemes.AScan)
        {
            // The waveform shows one selected channel; 2000 points is plenty for a 500-sample record
            // and the UI does not need more than ~30 refreshes per second.
            Needs = new DisplayNeeds
            {
                Channels = DisplayChannelSelection.Selected,
                MaxPointsAcross = 2000,
                MaxRefreshHz = 30,
            },
        },
        new ViewDescriptor(GateTableViewId, "闸门结果", ViewKinds.GateTable, AcquisitionSchemes.AScan),
    };

    public void Register(IServiceCollection services)
    {
        // Kept for plugins that depend on another plugin's services through DI. The shell itself
        // collects views through PluginHost.GetActive<IViewContribution>() (rule R11), not from DI.
        services.AddSingleton<IViewContribution>(this);
    }
}
