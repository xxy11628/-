using System.Diagnostics;
using System.Threading.Channels;
using UltrasoundHost.Contracts;

namespace UltrasoundHost.Plugins.Device.Simulator;

internal sealed class SimulatorDevice : IDevice
{
    private const double EchoSigmaSeconds = 0.25e-6;
    private const double MainBangDecaySeconds = 0.3e-6;
    private static readonly double DefaultFullScaleCounts = AdcFormat.Default.FullScaleCounts;
    private static readonly double MainBangAmplitude = 0.9 * DefaultFullScaleCounts;

    private readonly SimulatorOptions _options;
    private readonly AdcFormat _adc;
    private readonly double _adcScale;
    private readonly object _gate = new();
    private AcquisitionParameters? _parameters;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public SimulatorDevice(SimulatorOptions options)
    {
        options.Validate();
        _options = options;
        _adc = options.Adc;
        _adcScale = _adc.FullScaleCounts / DefaultFullScaleCounts;
        int channels = options.ReceiveChannels;
        Id = "simulator-" + options.Seed;
        Probe = channels == 1 ? ProbeModel.Single() : ProbeModel.Linear(channels, 0.6e-3);
        Capabilities = new DeviceCapabilities
        {
            Model = "Simulator",
            ElementCount = IntRange.Exactly(channels),
            ReceiveChannels = new IntRange(1, channels),
            Schemes = AcquisitionSchemes.AScan,
            Topologies = channels == 1 ? ProbeTopologies.Single : ProbeTopologies.Linear,
            ThroughputMBps = 1000,
            SupportsMultiBoardSync = false,
            SamplesPerRecord = new IntRange(16, 16384),
            Adc = _adc,
            MaxSampleRateHz = 100e6,
            MaxPrfHz = 10000,
        };
    }

    public string Id { get; }

    public DeviceCapabilities Capabilities { get; }

    public ProbeModel Probe { get; }

    public Task ApplyAsync(AcquisitionParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        IReadOnlyList<string> problems = parameters.ValidateAgainst(Capabilities);
        if (problems.Count > 0)
        {
            throw new ArgumentException("invalid parameters: " + string.Join("; ", problems), nameof(parameters));
        }

        lock (_gate)
        {
            AcquisitionParameters? current = _parameters;
            if (current is not null && parameters.Version <= current.Version)
            {
                throw new InvalidOperationException(
                    "parameter version " + parameters.Version + " is not higher than the applied version " + current.Version);
            }

            Volatile.Write(ref _parameters, parameters);
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(ChannelWriter<AcquisitionFrame> sink, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            if (_task is not null)
            {
                throw new InvalidOperationException("the device is already started");
            }

            if (_parameters is null)
            {
                throw new InvalidOperationException("apply parameters before starting");
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = _cts.Token;
            _task = Task.Run(() => RunAsync(sink, token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            task = _task;
            cts = _cts;
            _task = null;
            _cts = null;
        }

        if (task is null || cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(ChannelWriter<AcquisitionFrame> sink, CancellationToken cancellationToken)
    {
        var random = new Random(_options.Seed);
        Stopwatch clock = Stopwatch.StartNew();
        long sequence = 0;
        long sequenceOffset = 0;
        double nextSeconds = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested && (_options.MaxFrames <= 0 || sequence < _options.MaxFrames))
            {
                if (_options.ThrowAfterFrames > 0 && sequence >= _options.ThrowAfterFrames)
                {
                    throw new InvalidOperationException("injected simulator device failure after " + sequence + " frame(s)");
                }

                AcquisitionParameters parameters = Volatile.Read(ref _parameters)!;
                if (_options.SequenceGapAtFrame >= 0 && sequence == _options.SequenceGapAtFrame)
                {
                    sequenceOffset += _options.SequenceGapSize;
                }

                AcquisitionFrame frame = Generate(parameters, sequence + sequenceOffset, clock.Elapsed, random);
                if (_options.InjectDeviceOverflowEvery > 0
                    && sequence > 0
                    && sequence % _options.InjectDeviceOverflowEvery == 0)
                {
                    frame.Flags |= FrameFlags.DeviceOverflow;
                }

                bool handedOver = false;
                try
                {
                    if (_options.WriteDelayMilliseconds > 0)
                    {
                        await Task.Delay(_options.WriteDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                    }

                    await sink.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                    handedOver = true;
                }
                catch (ChannelClosedException)
                {
                    return;
                }
                finally
                {
                    if (!handedOver)
                    {
                        frame.Dispose();
                    }
                }

                sequence++;
                if (_options.Paced)
                {
                    double rate = _options.MaxFrameRateHz > 0 ? Math.Min(parameters.PrfHz, _options.MaxFrameRateHz) : parameters.PrfHz;
                    nextSeconds += 1.0 / rate;
                    double wait = nextSeconds - clock.Elapsed.TotalSeconds;
                    if (wait > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false);
                    }
                    else if (wait < -1.0)
                    {
                        nextSeconds = clock.Elapsed.TotalSeconds;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private AcquisitionFrame Generate(AcquisitionParameters parameters, long sequence, TimeSpan now, Random random)
    {
        AcquisitionFrame frame = AcquisitionFrame.Rent(FrameKind.AScan, 1, _options.ReceiveChannels, parameters.SamplesPerRecord);
        try
        {
            frame.Sequence = sequence;
            frame.TimestampNs = now.Ticks * 100;
            frame.ParamVersion = _options.EmitUnknownParamVersion ? parameters.Version + 100 : parameters.Version;
            frame.SampleRateHz = parameters.SampleRateHz;
            frame.StartDelaySeconds = parameters.StartDelaySeconds;
            frame.Adc = _adc;
            for (int channel = 0; channel < _options.ReceiveChannels; channel++)
            {
                Synthesize(frame.Span(0, channel), channel, parameters, random);
            }

            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private void Synthesize(Span<short> destination, int channel, AcquisitionParameters parameters, Random random)
    {
        double gain = Math.Pow(10.0, parameters.GainDb / 20.0);
        double speed = parameters.SoundSpeedMps;
        double defectTime = 2.0 * (_options.DefectDepthMeters + (channel * _options.DefectDepthStepMeters)) / speed;
        double backwallTime = 2.0 * _options.ThicknessMeters / speed;
        for (int n = 0; n < destination.Length; n++)
        {
            double t = parameters.StartDelaySeconds + (n / parameters.SampleRateHz);
            double value = MainBang(t)
                + Echo(t, defectTime, _options.DefectAmplitude)
                + Echo(t, backwallTime, _options.BackwallAmplitude)
                + (Gaussian(random) * _options.NoiseRmsCounts);
            value *= gain * _adcScale;
            destination[n] = (short)Math.Clamp(Math.Round(value), _adc.MinCounts, _adc.MaxCounts);
        }
    }

    private double MainBang(double t)
    {
        if (t < 0)
        {
            return 0;
        }

        return MainBangAmplitude * Math.Exp(-t / MainBangDecaySeconds) * Math.Sin(2.0 * Math.PI * _options.CenterFrequencyHz * t);
    }

    // A Gaussian-windowed cosine centred on t0, so the largest sample sits at the echo arrival time.
    private double Echo(double t, double t0, double amplitude)
    {
        double d = t - t0;
        if (Math.Abs(d) > 4.0 * EchoSigmaSeconds)
        {
            return 0;
        }

        double window = Math.Exp(-(d / EchoSigmaSeconds) * (d / EchoSigmaSeconds));
        return amplitude * window * Math.Cos(2.0 * Math.PI * _options.CenterFrequencyHz * d);
    }

    // Sum of 12 uniforms minus 6: close enough to N(0,1) for a noise floor.
    private static double Gaussian(Random random)
    {
        double sum = 0;
        for (int i = 0; i < 12; i++)
        {
            sum += random.NextDouble();
        }

        return sum - 6.0;
    }
}
