using UltrasoundHost.Contracts;

namespace UltrasoundHost.Plugins.Device.SerialUdp;

/// <summary>
/// Settings for the serial (control) + UDP (data) device plugin. Until a real board and protocol
/// v0.1 are confirmed, only <see cref="Loopback"/> mode is implemented; the real transports are
/// stubs that fail with a clear message (M3 gap).
/// </summary>
public sealed record SerialUdpOptions
{
    public const string DeviceType = "serial-udp";

    /// <summary>Use the in-process loopback board instead of real serial/UDP transports.</summary>
    public bool Loopback { get; init; } = true;

    public string SerialPort { get; init; } = string.Empty;

    public int BaudRate { get; init; } = 921600;

    public string DataHost { get; init; } = "127.0.0.1";

    public int DataPort { get; init; } = 5000;

    /// <summary>Upper bound of receive channels the loopback board reports.</summary>
    public int MaxChannels { get; init; } = 16;

    public long MaxFrames { get; init; }

    /// <summary>Pace the loopback board; unpaced generation still yields between frames to avoid a flood.</summary>
    public bool Paced { get; init; } = true;

    /// <summary>Test-only: skip every Nth datagram (the sequence number still advances).</summary>
    public long DropEveryNthFrame { get; init; }

    /// <summary>Test-only: corrupt every Nth datagram so the device rejects it (sequence gap follows).</summary>
    public long CorruptEveryNthFrame { get; init; }

    public static SerialUdpOptions FromSettings(DeviceOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = new SerialUdpOptions
        {
            Loopback = options.GetBool("loopback", true),
            SerialPort = options.Settings.TryGetValue("serialPort", out string? serialPort) ? serialPort : string.Empty,
            BaudRate = options.GetInt("baudRate", 921600),
            DataHost = options.Settings.TryGetValue("dataHost", out string? dataHost) ? dataHost : "127.0.0.1",
            DataPort = options.GetInt("dataPort", 5000),
            MaxChannels = options.GetInt("maxChannels", 16),
            MaxFrames = options.GetInt("maxFrames", 0),
            Paced = options.GetBool("paced", true),
            DropEveryNthFrame = options.GetInt("dropEvery", 0),
            CorruptEveryNthFrame = options.GetInt("corruptEvery", 0),
        };
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (MaxChannels < 1 || MaxChannels > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxChannels), "maxChannels must be in 1..4096");
        }

        if (DropEveryNthFrame < 0 || CorruptEveryNthFrame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DropEveryNthFrame));
        }

        if (DataPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(DataPort));
        }
    }
}
