using System.Globalization;
using System.Threading.Channels;

namespace UltrasoundHost.Contracts;

/// <summary>Free-form settings for opening a device (address, port, channel count, ...).</summary>
public sealed record DeviceOpenOptions
{
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    public int GetInt(string key, int fallback)
    {
        if (!Settings.TryGetValue(key, out string? text))
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new ArgumentException("setting '" + key + "' must be an integer, got '" + text + "'");
        }

        return value;
    }

    public bool GetBool(string key, bool fallback)
    {
        if (!Settings.TryGetValue(key, out string? text))
        {
            return fallback;
        }

        if (!bool.TryParse(text, out bool value))
        {
            throw new ArgumentException("setting '" + key + "' must be true or false, got '" + text + "'");
        }

        return value;
    }
}

/// <summary>Provided by device plugins. One factory per kind of transport or board.</summary>
public interface IDeviceFactory
{
    /// <summary>Short stable name such as "simulator", "serial-udp", "pcie".</summary>
    string DeviceType { get; }

    Task<IDevice> OpenAsync(DeviceOpenOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// A device plugin's contribution to the application. Exposed by
/// <c>PluginHost.GetActive&lt;IDeviceFactoryContribution&gt;()</c> only while the plugin is Running,
/// so a plugin that failed to start never hands a factory to the shell.
/// </summary>
public interface IDeviceFactoryContribution
{
    IDeviceFactory Factory { get; }
}

/// <summary>A connected board. Transport details (serial, UDP, PCIe) stay behind this interface.</summary>
public interface IDevice : IAsyncDisposable
{
    string Id { get; }

    DeviceCapabilities Capabilities { get; }

    ProbeModel Probe { get; }

    /// <summary>
    /// Applies new settings. The version must be higher than the last applied one. Frames acquired
    /// afterwards carry the new version.
    /// </summary>
    Task ApplyAsync(AcquisitionParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts acquisition into <paramref name="sink"/>. The caller picks the channel policy (drop old
    /// frames for display, apply back-pressure for recording) and owns completing the writer.
    /// Frames the device cannot hand over are disposed by the device.
    /// </summary>
    Task StartAsync(ChannelWriter<AcquisitionFrame> sink, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
