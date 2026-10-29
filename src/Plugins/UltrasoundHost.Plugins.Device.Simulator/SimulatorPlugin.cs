using Microsoft.Extensions.DependencyInjection;
using UltrasoundHost.Contracts;

namespace UltrasoundHost.Plugins.Device.Simulator;

/// <summary>Device plugin that fakes a board, so the whole chain runs without hardware.</summary>
public sealed class SimulatorPlugin : IPlugin, IDeviceFactoryContribution
{
    public const string PluginId = "device.simulator";

    private readonly SimulatorDeviceFactory _factory = new();

    public PluginManifest Manifest { get; } = new PluginManifest(PluginId, new Version(0, 1, 0), PluginKind.Device, "模拟板卡")
    {
        DeviceProvides = new DeviceCapabilities
        {
            Model = "Simulator",
            ElementCount = new IntRange(1, 4096),
            ReceiveChannels = new IntRange(1, 4096),
            Schemes = AcquisitionSchemes.AScan,
            Topologies = ProbeTopologies.Single | ProbeTopologies.Linear,
            ThroughputMBps = 1000,
            SamplesPerRecord = new IntRange(16, 16384),
            MaxSampleRateHz = 100e6,
            MaxPrfHz = 10000,
        },
    };

    public IDeviceFactory Factory => _factory;

    public void Register(IServiceCollection services)
    {
        // Kept for plugins that depend on another plugin's services through DI.
        services.AddSingleton<IDeviceFactory>(_factory);
    }
}
