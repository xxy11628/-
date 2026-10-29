using System.Threading.Channels;
using UltrasoundHost.Contracts;

namespace UltrasoundHost.Plugins.Device.SerialUdp;

/// <summary>
/// In-process stand-in for the real board (08 M3 deliverable): answers the control channel,
/// generates data-channel datagrams and can inject loss/corruption so the device plugin is testable
/// without hardware. It implements both transports from the client's point of view.
/// </summary>
public sealed class LoopbackBoard : IControlTransport, IDataTransport
{
    private readonly SerialUdpOptions _options;
    private readonly DeviceCapabilities _capabilities;
    private readonly Channel<byte[]> _requests = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<byte[]> _datagrams = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _controlTask;
    private readonly object _gate = new();
    private readonly List<CancellationTokenSource> _retiredCancellations = new();

    private SerialUdpConfigure? _configure;
    private CancellationTokenSource? _dataCancellation;
    private Task? _dataTask;
    private long _sequence;

    public LoopbackBoard(SerialUdpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _capabilities = new DeviceCapabilities
        {
            Model = "Loopback serial-udp board",
            ElementCount = new IntRange(1, options.MaxChannels),
            ReceiveChannels = new IntRange(1, options.MaxChannels),
            Schemes = AcquisitionSchemes.AScan,
            Topologies = ProbeTopologies.Single | ProbeTopologies.Linear,
            SamplesPerRecord = new IntRange(16, 16384),
            MaxSampleRateHz = 100e6,
            MaxPrfHz = 10000,
            ThroughputMBps = 1000,
            Adc = AdcFormat.Default,
        };
        _controlTask = Task.Run(ControlLoopAsync);
    }

    public DeviceCapabilities Capabilities => _capabilities;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        _requests.Writer.TryWrite(bytes.ToArray());
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        byte[] response = await _responses.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        response.CopyTo(buffer);
        return response.Length;
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        byte[] datagram = await _datagrams.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        datagram.CopyTo(buffer);
        return datagram.Length;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _requests.Writer.TryComplete();
        _responses.Writer.TryComplete();
        _datagrams.Writer.TryComplete();

        CancellationTokenSource? dataCancellation;
        Task? dataTask;
        lock (_gate)
        {
            dataCancellation = _dataCancellation;
            dataTask = _dataTask;
            _dataCancellation = null;
            _dataTask = null;
        }

        dataCancellation?.Cancel();
        await AwaitQuietlyAsync(_controlTask).ConfigureAwait(false);
        if (dataTask is not null)
        {
            await AwaitQuietlyAsync(dataTask).ConfigureAwait(false);
        }

        dataCancellation?.Dispose();
        lock (_gate)
        {
            foreach (CancellationTokenSource retired in _retiredCancellations)
            {
                retired.Dispose();
            }

            _retiredCancellations.Clear();
        }

        _lifetime.Dispose();
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ControlLoopAsync()
    {
        try
        {
            while (await _requests.Reader.WaitToReadAsync(_lifetime.Token).ConfigureAwait(false))
            {
                while (_requests.Reader.TryRead(out byte[]? request))
                {
                    if (request is not null)
                    {
                        HandleRequest(request);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void HandleRequest(byte[] request)
    {
        if (!SerialUdpProtocol.TryDecodeControlRequest(request, out byte command, out ReadOnlySpan<byte> payload))
        {
            return;
        }

        switch (command)
        {
            case SerialUdpProtocol.CommandIdentify:
                Respond(command, SerialUdpProtocol.StatusOk, SerialUdpProtocol.EncodeCapabilities(_capabilities));
                break;

            case SerialUdpProtocol.CommandConfigure:
                if (!SerialUdpProtocol.TryDecodeConfigure(payload, out SerialUdpConfigure configure)
                    || configure.SamplesPerRecord < 1
                    || configure.ActiveReceiveChannels < 1
                    || configure.ActiveReceiveChannels > _options.MaxChannels)
                {
                    Respond(command, SerialUdpProtocol.StatusBadParameter, Array.Empty<byte>());
                    break;
                }

                _configure = configure;
                Respond(command, SerialUdpProtocol.StatusOk, Array.Empty<byte>());
                break;

            case SerialUdpProtocol.CommandStart:
                if (_configure is null)
                {
                    Respond(command, SerialUdpProtocol.StatusBadParameter, Array.Empty<byte>());
                    break;
                }

                StartData();
                Respond(command, SerialUdpProtocol.StatusOk, Array.Empty<byte>());
                break;

            case SerialUdpProtocol.CommandStop:
                StopData();
                Respond(command, SerialUdpProtocol.StatusOk, Array.Empty<byte>());
                break;

            default:
                Respond(command, SerialUdpProtocol.StatusBadCommand, Array.Empty<byte>());
                break;
        }
    }

    private void Respond(byte command, byte status, byte[] payload) =>
        _responses.Writer.TryWrite(SerialUdpProtocol.EncodeControlResponse(command, status, payload));

    private void StartData()
    {
        lock (_gate)
        {
            if (_dataTask is { IsCompleted: false })
            {
                return;
            }

            _dataCancellation = new CancellationTokenSource();
            _dataTask = Task.Run(() => DataLoopAsync(_dataCancellation.Token));
        }
    }

    private void StopData()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _dataCancellation;
            _dataCancellation = null;
            if (cancellation is not null)
            {
                // The data loop may still be reading the token; dispose it with the board instead.
                _retiredCancellations.Add(cancellation);
            }
        }

        cancellation?.Cancel();
    }

    private async Task DataLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            long emitted = 0;
            while (!cancellationToken.IsCancellationRequested && (_options.MaxFrames <= 0 || emitted < _options.MaxFrames))
            {
                SerialUdpConfigure configure = _configure ?? throw new InvalidOperationException("board is not configured");
                long sequence = _sequence++;
                using AcquisitionFrame frame = Synthesize(configure, sequence);
                byte[] datagram = SerialUdpProtocol.EncodeDataFrame(frame);

                bool drop = _options.DropEveryNthFrame > 0 && (emitted + 1) % _options.DropEveryNthFrame == 0;
                bool corrupt = _options.CorruptEveryNthFrame > 0 && (emitted + 1) % _options.CorruptEveryNthFrame == 0;
                if (!drop)
                {
                    if (corrupt && datagram.Length > 0)
                    {
                        datagram[^1] ^= 0xFF;
                    }

                    if (!_datagrams.Writer.TryWrite(datagram))
                    {
                        return;
                    }
                }

                emitted++;
                if (_options.Paced)
                {
                    double rate = Math.Min(configure.PrfHz, 200);
                    if (rate > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1.0 / rate), cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    // Keep unpaced tests bounded; a real UDP socket would push back on its own.
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private AcquisitionFrame Synthesize(SerialUdpConfigure configure, long sequence)
    {
        int channels = configure.ActiveReceiveChannels;
        AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, channels, configure.SamplesPerRecord);
        frame.Sequence = sequence;
        frame.ParamVersion = configure.Version;
        frame.SampleRateHz = configure.SampleRateHz;
        frame.StartDelaySeconds = configure.StartDelaySeconds;
        frame.Adc = AdcFormat.Default;

        double gain = Math.Pow(10.0, configure.GainDb / 20.0);
        double speed = Math.Max(1.0, configure.SoundSpeedMps);
        for (int channel = 0; channel < channels; channel++)
        {
            Span<short> destination = frame.Span(0, channel);
            double defectTime = 2.0 * (0.010 + (channel * 0.0005)) / speed;
            double backwallTime = 2.0 * 0.020 / speed;
            for (int sample = 0; sample < destination.Length; sample++)
            {
                double t = configure.StartDelaySeconds + (sample / configure.SampleRateHz);
                double mainBang = t < 0 ? 0 : 0.9 * 29491.0 * Math.Exp(-t / 0.3e-6) * Math.Sin(2.0 * Math.PI * 5e6 * t);
                double defect = Echo(t, defectTime, 9000, 5e6);
                double backwall = Echo(t, backwallTime, 16000, 5e6);
                destination[sample] = (short)Math.Clamp(Math.Round((mainBang + defect + backwall) * gain), short.MinValue, short.MaxValue);
            }
        }

        return frame;
    }

    private static double Echo(double t, double t0, double amplitude, double frequency)
    {
        const double sigma = 0.25e-6;
        double delta = t - t0;
        if (Math.Abs(delta) > 4.0 * sigma)
        {
            return 0;
        }

        double window = Math.Exp(-(delta / sigma) * (delta / sigma));
        return amplitude * window * Math.Cos(2.0 * Math.PI * frequency * delta);
    }
}
