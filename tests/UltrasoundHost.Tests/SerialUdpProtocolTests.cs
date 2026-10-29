using System.Runtime.InteropServices;
using UltrasoundHost.Contracts;
using UltrasoundHost.Plugins.Device.SerialUdp;

namespace UltrasoundHost.Tests;

/// <summary>Byte-level protocol v0.1 draft round-trips (docs/protocol/v0.1.md).</summary>
[Collection("FramePool")]
public class SerialUdpProtocolTests
{
    [Fact]
    public void A_control_request_round_trips_and_rejects_a_bad_crc()
    {
        byte[] payload = { 1, 2, 3, 4 };
        byte[] request = SerialUdpProtocol.EncodeControlRequest(SerialUdpProtocol.CommandConfigure, payload);

        Assert.True(SerialUdpProtocol.TryDecodeControlRequest(request, out byte command, out ReadOnlySpan<byte> decoded));
        Assert.Equal(SerialUdpProtocol.CommandConfigure, command);
        Assert.Equal(payload, decoded.ToArray());

        request[^1] ^= 0xFF;
        Assert.False(SerialUdpProtocol.TryDecodeControlRequest(request, out _, out _));
    }

    [Fact]
    public void A_control_response_round_trips_with_status()
    {
        byte[] response = SerialUdpProtocol.EncodeControlResponse(
            SerialUdpProtocol.CommandIdentify,
            SerialUdpProtocol.StatusOk,
            new byte[] { 9, 8 });

        Assert.True(SerialUdpProtocol.TryDecodeControlResponse(
            response,
            out byte command,
            out byte status,
            out ReadOnlySpan<byte> payload));
        Assert.Equal(SerialUdpProtocol.CommandIdentify, command);
        Assert.Equal(SerialUdpProtocol.StatusOk, status);
        Assert.Equal(new byte[] { 9, 8 }, payload.ToArray());
    }

    [Fact]
    public void Capabilities_round_trip_through_the_control_payload()
    {
        var capabilities = new DeviceCapabilities
        {
            Model = "Test board",
            ElementCount = new IntRange(1, 64),
            ReceiveChannels = new IntRange(2, 32),
            Schemes = AcquisitionSchemes.AScan,
            Topologies = ProbeTopologies.Single | ProbeTopologies.Linear,
            SamplesPerRecord = new IntRange(16, 8192),
            MaxSampleRateHz = 80e6,
            MaxPrfHz = 5000,
            ThroughputMBps = 1200,
            Adc = new AdcFormat { Bits = 14 },
            SupportsMultiBoardSync = true,
        };

        DeviceCapabilities decoded = SerialUdpProtocol.DecodeCapabilities(SerialUdpProtocol.EncodeCapabilities(capabilities));

        Assert.Equal(capabilities.Model, decoded.Model);
        Assert.Equal(capabilities.ElementCount, decoded.ElementCount);
        Assert.Equal(capabilities.ReceiveChannels, decoded.ReceiveChannels);
        Assert.Equal(capabilities.Schemes, decoded.Schemes);
        Assert.Equal(capabilities.Topologies, decoded.Topologies);
        Assert.Equal(capabilities.SamplesPerRecord, decoded.SamplesPerRecord);
        Assert.Equal(capabilities.MaxSampleRateHz, decoded.MaxSampleRateHz);
        Assert.Equal(capabilities.MaxPrfHz, decoded.MaxPrfHz);
        Assert.Equal(capabilities.ThroughputMBps, decoded.ThroughputMBps);
        Assert.Equal(14, decoded.Adc.Bits);
        Assert.True(decoded.SupportsMultiBoardSync);
    }

    [Fact]
    public void Configure_round_trips_the_parameters_the_board_needs()
    {
        var parameters = new AcquisitionParameters
        {
            Version = 7,
            SampleRateHz = 50e6,
            SamplesPerRecord = 500,
            PrfHz = 1000,
            GainDb = 6,
            SoundSpeedMps = 5900,
            StartDelaySeconds = 1e-6,
            ActiveReceiveChannels = 2,
        };

        Assert.True(SerialUdpProtocol.TryDecodeConfigure(SerialUdpProtocol.EncodeConfigure(parameters), out SerialUdpConfigure configure));
        Assert.Equal(7, configure.Version);
        Assert.Equal(parameters.SampleRateHz, configure.SampleRateHz);
        Assert.Equal(parameters.SamplesPerRecord, configure.SamplesPerRecord);
        Assert.Equal(parameters.PrfHz, configure.PrfHz);
        Assert.Equal(parameters.GainDb, configure.GainDb);
        Assert.Equal(parameters.SoundSpeedMps, configure.SoundSpeedMps);
        Assert.Equal(parameters.StartDelaySeconds, configure.StartDelaySeconds);
        Assert.Equal(parameters.ActiveReceiveChannels, configure.ActiveReceiveChannels);
    }

    [Fact]
    public void A_data_frame_round_trips_header_and_samples_and_rejects_a_bad_crc()
    {
        using AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, 2, 16);
        frame.Sequence = 42;
        frame.ParamVersion = 3;
        frame.Adc = new AdcFormat { Bits = 14 };
        frame.Flags = FrameFlags.DeviceOverflow;
        for (int channel = 0; channel < frame.Rx; channel++)
        {
            for (int sample = 0; sample < frame.Samples; sample++)
            {
                frame.Span(0, channel)[sample] = (short)((channel * 100) + sample);
            }
        }

        byte[] datagram = SerialUdpProtocol.EncodeDataFrame(frame);

        Assert.True(SerialUdpProtocol.TryDecodeDataFrame(datagram, out SerialUdpDataHeader header, out ReadOnlySpan<byte> payload));
        Assert.Equal(42, header.Sequence);
        Assert.Equal(3, header.ParamVersion);
        Assert.Equal(1, header.Tx);
        Assert.Equal(2, header.Rx);
        Assert.Equal(16, header.Samples);
        Assert.Equal(14, header.Adc.Bits);
        Assert.Equal(FrameFlags.DeviceOverflow, header.Flags);
        Assert.Equal(1, header.FragmentCount);
        Assert.Equal(frame.Length * 2, payload.Length);
        Assert.Equal(frame.Data.ToArray(), MemoryMarshal.Cast<byte, short>(payload).ToArray());

        datagram[^1] ^= 0xFF;
        Assert.False(SerialUdpProtocol.TryDecodeDataFrame(datagram, out _, out _));
    }
}
