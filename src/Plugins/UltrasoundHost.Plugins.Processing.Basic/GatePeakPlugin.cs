using Microsoft.Extensions.DependencyInjection;
using UltrasoundHost.Contracts;

namespace UltrasoundHost.Plugins.Processing.Basic;

/// <summary>Processing plugin: amplitude gate and peak detection for A-scans.</summary>
public sealed class GatePeakPlugin : IPlugin, IProcessorContribution
{
    public const string PluginId = "processing.basic";

    private readonly GatePeakProcessor _processor = new();

    public PluginManifest Manifest { get; } = new PluginManifest(PluginId, new Version(0, 1, 0), PluginKind.Processing, "闸门与峰值检测")
    {
        DeviceNeeds = new DeviceRequirements { Schemes = AcquisitionSchemes.AScan },
    };

    public IFrameProcessor CreateProcessor() => _processor;

    public void Register(IServiceCollection services)
    {
        // Kept for plugins that depend on another plugin's services through DI.
        services.AddSingleton<IFrameProcessor>(_processor);
    }
}
