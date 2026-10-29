using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UltrasoundHost.Contracts;

namespace UltrasoundHost.Plugins.Device.SerialUdp;

/// <summary>Serial control transport (client side of the control channel).</summary>
public interface IControlTransport : IAsyncDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>Ethernet data transport (UDP datagrams received from the board).</summary>
public interface IDataTransport : IAsyncDisposable
{
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>
/// Byte-level protocol v0.1 draft (docs/protocol/v0.1.md). Control channel frames are
/// <c>AA 55 cmd [status] len16 payload crc16</c>; data channel datagrams are a 40-byte header
/// followed by int16 samples in [tx][rx][sample] order, little endian, with CRC32.
/// </summary>
public static class SerialUdpProtocol
{
    public const ushort ControlMagic = 0x55AA;
    public const byte CommandIdentify = 0x01;
    public const byte CommandConfigure = 0x02;
    public const byte CommandStart = 0x03;
    public const byte CommandStop = 0x04;
    public const byte ResponseFlag = 0x80;
    public const byte StatusOk = 0;
    public const byte StatusBadCommand = 1;
    public const byte StatusBadChecksum = 2;
    public const byte StatusBadParameter = 3;
    public const byte StatusBusy = 4;

    public const uint DataMagic = 0x544C5355;
    public const ushort ProtocolVersion = 1;
    public const int HeaderSize = 40;
    public const int MaxDatagramPayload = 65507 - HeaderSize;

    // --- control channel -------------------------------------------------

    public static byte[] EncodeControlRequest(byte command, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[7 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, ControlMagic);
        frame[2] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(5));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5 + payload.Length), Crc16(frame.AsSpan(2, 3 + payload.Length)));
        return frame;
    }

    public static byte[] EncodeControlResponse(byte command, byte status, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, ControlMagic);
        frame[2] = (byte)(command | ResponseFlag);
        frame[3] = status;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(6));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6 + payload.Length), Crc16(frame.AsSpan(2, 4 + payload.Length)));
        return frame;
    }

    public static bool TryDecodeControlRequest(ReadOnlySpan<byte> frame, out byte command, out ReadOnlySpan<byte> payload)
    {
        command = 0;
        payload = default;
        if (frame.Length < 7 || BinaryPrimitives.ReadUInt16LittleEndian(frame) != ControlMagic)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(frame[3..]);
        if (frame.Length != 7 + length)
        {
            return false;
        }

        if (Crc16(frame[2..(5 + length)]) != BinaryPrimitives.ReadUInt16LittleEndian(frame[(5 + length)..]))
        {
            return false;
        }

        command = frame[2];
        payload = frame.Slice(5, length);
        return true;
    }

    public static bool TryDecodeControlResponse(ReadOnlySpan<byte> frame, out byte command, out byte status, out ReadOnlySpan<byte> payload)
    {
        command = 0;
        status = 0;
        payload = default;
        if (frame.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(frame) != ControlMagic)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]);
        if (frame.Length != 8 + length)
        {
            return false;
        }

        if (Crc16(frame[2..(6 + length)]) != BinaryPrimitives.ReadUInt16LittleEndian(frame[(6 + length)..]))
        {
            return false;
        }

        command = (byte)(frame[2] & ~ResponseFlag);
        status = frame[3];
        payload = frame.Slice(6, length);
        return true;
    }

    // --- control payloads -------------------------------------------------

    public static byte[] EncodeCapabilities(DeviceCapabilities capabilities)
    {
        byte[] model = System.Text.Encoding.ASCII.GetBytes(capabilities.Model);
        var payload = new byte[1 + model.Length + 8 + 28 + 4];
        int offset = 0;
        payload[offset++] = (byte)model.Length;
        model.CopyTo(payload.AsSpan(offset));
        offset += model.Length;
        WriteU16(payload, ref offset, (ushort)capabilities.ElementCount.Min);
        WriteU16(payload, ref offset, (ushort)capabilities.ElementCount.Max);
        WriteU16(payload, ref offset, (ushort)capabilities.ReceiveChannels.Min);
        WriteU16(payload, ref offset, (ushort)capabilities.ReceiveChannels.Max);
        WriteU32(payload, ref offset, (uint)capabilities.Schemes);
        WriteU32(payload, ref offset, (uint)capabilities.Topologies);
        WriteU32(payload, ref offset, (uint)capabilities.SamplesPerRecord.Min);
        WriteU32(payload, ref offset, (uint)capabilities.SamplesPerRecord.Max);
        WriteU32(payload, ref offset, (uint)Math.Min(uint.MaxValue, capabilities.MaxSampleRateHz));
        WriteU32(payload, ref offset, (uint)Math.Min(uint.MaxValue, capabilities.MaxPrfHz));
        WriteU32(payload, ref offset, (uint)Math.Min(uint.MaxValue, capabilities.ThroughputMBps));
        payload[offset++] = (byte)capabilities.Adc.Bits;
        payload[offset++] = capabilities.Adc.Signed ? (byte)1 : (byte)0;
        payload[offset++] = capabilities.Adc.RightAligned ? (byte)1 : (byte)0;
        payload[offset] = capabilities.SupportsMultiBoardSync ? (byte)1 : (byte)0;
        return payload;
    }

    public static DeviceCapabilities DecodeCapabilities(ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        int modelLength = payload[offset++];
        string model = System.Text.Encoding.ASCII.GetString(payload.Slice(offset, modelLength));
        offset += modelLength;
        var element = new IntRange(ReadU16(payload, ref offset), ReadU16(payload, ref offset));
        var receive = new IntRange(ReadU16(payload, ref offset), ReadU16(payload, ref offset));
        var schemes = (AcquisitionSchemes)ReadU32(payload, ref offset);
        var topologies = (ProbeTopologies)ReadU32(payload, ref offset);
        var samples = new IntRange((int)ReadU32(payload, ref offset), (int)ReadU32(payload, ref offset));
        double maxSampleRate = ReadU32(payload, ref offset);
        double maxPrf = ReadU32(payload, ref offset);
        double throughput = ReadU32(payload, ref offset);
        var adc = new AdcFormat
        {
            Bits = payload[offset++],
            Signed = payload[offset++] != 0,
            RightAligned = payload[offset++] != 0,
        };
        bool multiSync = payload[offset] != 0;
        return new DeviceCapabilities
        {
            Model = model,
            ElementCount = element,
            ReceiveChannels = receive,
            Schemes = schemes,
            Topologies = topologies,
            SamplesPerRecord = samples,
            MaxSampleRateHz = maxSampleRate,
            MaxPrfHz = maxPrf,
            ThroughputMBps = throughput,
            Adc = adc,
            SupportsMultiBoardSync = multiSync,
        };
    }

    /// <summary>Configure payload: version, sample rate, samples/record, PRF, gain, speed of sound, start delay, active channels.</summary>
    public static byte[] EncodeConfigure(AcquisitionParameters parameters)
    {
        var payload = new byte[32];
        int offset = 0;
        WriteU32(payload, ref offset, (uint)parameters.Version);
        WriteU32(payload, ref offset, (uint)Math.Round(parameters.SampleRateHz));
        WriteU32(payload, ref offset, (uint)parameters.SamplesPerRecord);
        WriteU32(payload, ref offset, (uint)Math.Round(parameters.PrfHz));
        WriteI32(payload, ref offset, (int)Math.Round(parameters.GainDb * 1000.0));
        WriteU32(payload, ref offset, (uint)Math.Round(parameters.SoundSpeedMps));
        WriteU32(payload, ref offset, (uint)Math.Round(parameters.StartDelaySeconds * 1e9));
        WriteU16(payload, ref offset, (ushort)parameters.ActiveReceiveChannels);
        return payload;
    }

    public static bool TryDecodeConfigure(ReadOnlySpan<byte> payload, out SerialUdpConfigure configure)
    {
        configure = default;
        if (payload.Length < 32)
        {
            return false;
        }

        int offset = 0;
        configure = new SerialUdpConfigure(
            Version: (int)ReadU32(payload, ref offset),
            SampleRateHz: ReadU32(payload, ref offset),
            SamplesPerRecord: (int)ReadU32(payload, ref offset),
            PrfHz: ReadU32(payload, ref offset),
            GainDb: ReadI32(payload, ref offset) / 1000.0,
            SoundSpeedMps: ReadU32(payload, ref offset),
            StartDelaySeconds: ReadU32(payload, ref offset) / 1e9,
            ActiveReceiveChannels: ReadU16(payload, ref offset));
        return true;
    }

    // --- data channel -----------------------------------------------------

    public static byte[] EncodeDataFrame(AcquisitionFrame frame)
    {
        int sampleCount = frame.Length;
        int payloadBytes = sampleCount * 2;
        if (payloadBytes > MaxDatagramPayload)
        {
            throw new NotSupportedException("frame does not fit in one datagram; fragmentation is reserved for a later protocol revision");
        }

        var datagram = new byte[HeaderSize + payloadBytes];
        int offset = 0;
        WriteU32(datagram, ref offset, DataMagic);
        WriteU16(datagram, ref offset, ProtocolVersion);
        WriteU16(datagram, ref offset, HeaderSize);
        WriteU64(datagram, ref offset, (ulong)frame.Sequence);
        WriteU32(datagram, ref offset, (uint)frame.ParamVersion);
        WriteU16(datagram, ref offset, (ushort)frame.Tx);
        WriteU16(datagram, ref offset, (ushort)frame.Rx);
        WriteU32(datagram, ref offset, (uint)frame.Samples);
        datagram[offset++] = (byte)frame.Adc.Bits;
        datagram[offset++] = (byte)((frame.Adc.Signed ? 1 : 0) | (frame.Adc.RightAligned ? 2 : 0));
        datagram[offset++] = (byte)frame.Flags;
        datagram[offset++] = 0;
        WriteU16(datagram, ref offset, 0); // fragment index (reserved, always 0 in v0.1)
        WriteU16(datagram, ref offset, 1); // fragment count (reserved, always 1 in v0.1)
        WriteU32(datagram, ref offset, 0); // checksum placeholder

        MemoryMarshal.AsBytes(frame.Data.Span).CopyTo(datagram.AsSpan(HeaderSize));
        uint checksum = Crc32(datagram.AsSpan(0, HeaderSize - 4), datagram.AsSpan(HeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(HeaderSize - 4), checksum);
        return datagram;
    }

    public static bool TryDecodeDataFrame(
        ReadOnlySpan<byte> datagram,
        out SerialUdpDataHeader header,
        out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;
        if (datagram.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(datagram) != DataMagic)
        {
            return false;
        }

        int offset = 4;
        ushort version = ReadU16(datagram, ref offset);
        ushort headerSize = ReadU16(datagram, ref offset);
        if (version != ProtocolVersion || headerSize != HeaderSize)
        {
            return false;
        }

        ulong sequence = ReadU64(datagram, ref offset);
        uint paramVersion = ReadU32(datagram, ref offset);
        ushort tx = ReadU16(datagram, ref offset);
        ushort rx = ReadU16(datagram, ref offset);
        uint samples = ReadU32(datagram, ref offset);
        byte adcBits = datagram[offset++];
        byte adcFlags = datagram[offset++];
        var frameFlags = (FrameFlags)datagram[offset++];
        offset++; // reserved
        ushort fragmentIndex = ReadU16(datagram, ref offset);
        ushort fragmentCount = ReadU16(datagram, ref offset);
        ReadU32(datagram, ref offset); // checksum

        int payloadBytes = (int)(tx * rx * samples * 2L);
        if (payloadBytes <= 0 || datagram.Length != HeaderSize + payloadBytes)
        {
            return false;
        }

        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(HeaderSize - 4, 4));
        if (Crc32(datagram[..(HeaderSize - 4)], datagram[HeaderSize..]) != expected)
        {
            return false;
        }

        if (fragmentCount != 1)
        {
            return false; // fragmentation is reserved for a later protocol revision
        }

        header = new SerialUdpDataHeader(
            Sequence: (long)sequence,
            ParamVersion: (int)paramVersion,
            Tx: tx,
            Rx: rx,
            Samples: (int)samples,
            Adc: new AdcFormat
            {
                Bits = adcBits,
                Signed = (adcFlags & 1) != 0,
                RightAligned = (adcFlags & 2) != 0,
            },
            Flags: frameFlags,
            FragmentIndex: fragmentIndex,
            FragmentCount: fragmentCount);
        payload = datagram[HeaderSize..];
        return true;
    }

    // --- checksums ---------------------------------------------------------

    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (byte value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
        }

        return crc;
    }

    public static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = 0xFFFFFFFF;
        crc = UpdateCrc32(crc, first);
        crc = UpdateCrc32(crc, second);
        return ~crc;
    }

    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        return ~UpdateCrc32(0xFFFFFFFF, bytes);
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return crc;
    }

    private static void WriteU16(byte[] target, ref int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset), value);
        offset += 2;
    }

    private static void WriteU32(byte[] target, ref int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset), value);
        offset += 4;
    }

    private static void WriteI32(byte[] target, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset), value);
        offset += 4;
    }

    private static void WriteU64(byte[] target, ref int offset, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(target.AsSpan(offset), value);
        offset += 8;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> source, ref int offset)
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
        offset += 2;
        return value;
    }

    private static uint ReadU32(ReadOnlySpan<byte> source, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
        offset += 4;
        return value;
    }

    private static int ReadI32(ReadOnlySpan<byte> source, ref int offset)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);
        offset += 4;
        return value;
    }

    private static ulong ReadU64(ReadOnlySpan<byte> source, ref int offset)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);
        offset += 8;
        return value;
    }
}

public readonly record struct SerialUdpConfigure(
    int Version,
    double SampleRateHz,
    int SamplesPerRecord,
    double PrfHz,
    double GainDb,
    double SoundSpeedMps,
    double StartDelaySeconds,
    int ActiveReceiveChannels);

public readonly record struct SerialUdpDataHeader(
    long Sequence,
    int ParamVersion,
    int Tx,
    int Rx,
    int Samples,
    AdcFormat Adc,
    FrameFlags Flags,
    int FragmentIndex,
    int FragmentCount)
{
    public int PayloadBytes => Tx * Rx * Samples * 2;
}
