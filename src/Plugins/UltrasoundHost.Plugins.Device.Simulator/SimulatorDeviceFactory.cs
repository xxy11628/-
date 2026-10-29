using UltrasoundHost.Contracts;

namespace UltrasoundHost.Plugins.Device.Simulator;

public sealed class SimulatorDeviceFactory : IDeviceFactory
{
    public string DeviceType => "simulator";

    /// <summary>Settings understood: channels, seed, paced (true/false), maxFrames.</summary>
    public Task<IDevice> OpenAsync(DeviceOpenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.FromResult(Create(SimulatorOptions.FromSettings(options)));
    }

    /// <summary>Creates a device with full control over the simulated specimen (used by tests and tools).</summary>
    public IDevice Create(SimulatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SimulatorDevice(options);
    }
}
