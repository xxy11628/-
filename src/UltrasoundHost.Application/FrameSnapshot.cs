using UltrasoundHost.Contracts;

namespace UltrasoundHost.Application;

/// <summary>
/// An owned copy of what the display path was asked to show (05 §7). Only the requested channels
/// are copied and each channel may be peak-preservingly decimated. The copy is immutable, so the UI
/// can read it while the next frame is being prepared. Gate results are not part of it: those come
/// from the inspection path (<see cref="ShellSession.LatestInspection"/>).
/// </summary>
public sealed class FrameSnapshot
{
    private readonly double[][]? _sampleTimes;

    private FrameSnapshot(
        long sequence,
        int paramVersion,
        double sampleRateHz,
        double startDelaySeconds,
        double durationSeconds,
        AdcFormat adc,
        GateConfig gate,
        int[] channelNumbers,
        short[][] channels,
        double[][]? sampleTimes)
    {
        Sequence = sequence;
        ParamVersion = paramVersion;
        SampleRateHz = sampleRateHz;
        StartDelaySeconds = startDelaySeconds;
        DurationSeconds = durationSeconds;
        Adc = adc;
        Gate = gate;
        ChannelNumbers = channelNumbers;
        Channels = channels;
        _sampleTimes = sampleTimes;
    }

    public long Sequence { get; }

    public int ParamVersion { get; }

    public double SampleRateHz { get; }

    public double StartDelaySeconds { get; }

    /// <summary>Duration of the recorded window in seconds (before decimation).</summary>
    public double DurationSeconds { get; }

    /// <summary>ADC format of the samples, needed by the waveform view to scale to full scale.</summary>
    public AdcFormat Adc { get; }

    /// <summary>Gate window in effect when the frame was acquired, used only for drawing.</summary>
    public GateConfig Gate { get; }

    /// <summary>Actual receive-channel number of each entry in <see cref="Channels"/>.</summary>
    public int[] ChannelNumbers { get; }

    /// <summary>One sample array per requested channel, [entry][sample]. May be decimated.</summary>
    public short[][] Channels { get; }

    public int ChannelCount => Channels.Length;

    /// <summary>Position of a receive channel in this snapshot, or -1 when it was not requested.</summary>
    public int IndexOfChannel(int channel)
    {
        for (int index = 0; index < ChannelNumbers.Length; index++)
        {
            if (ChannelNumbers[index] == channel)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Time of one copied sample. For decimated channels this is the time of the original sample.</summary>
    public double SampleTimeSeconds(int channelIndex, int sampleIndex) =>
        _sampleTimes is not null
            ? _sampleTimes[channelIndex][sampleIndex]
            : StartDelaySeconds + (sampleIndex / SampleRateHz);

    internal static FrameSnapshot Capture(AcquisitionFrame frame, AcquisitionParameters parameters, DisplayRequest request)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<int> requested = request.ResolveChannels(frame.Rx);
        var channelNumbers = new int[requested.Count];
        var channels = new short[requested.Count][];
        var sampleTimes = request.MaxPointsAcross.HasValue ? new double[requested.Count][] : null;

        for (int index = 0; index < requested.Count; index++)
        {
            int channel = requested[index];
            channelNumbers[index] = channel;
            (short[] samples, double[]? times) = Resample(
                frame.Span(0, channel),
                frame.SampleRateHz,
                frame.StartDelaySeconds,
                request.MaxPointsAcross);
            channels[index] = samples;
            if (sampleTimes is not null)
            {
                sampleTimes[index] = times ?? BuildLinearTimes(samples.Length, frame.SampleRateHz, frame.StartDelaySeconds);
            }
        }

        double duration = frame.SampleRateHz > 0 ? frame.Samples / frame.SampleRateHz : 0;
        return new FrameSnapshot(
            frame.Sequence,
            frame.ParamVersion,
            frame.SampleRateHz,
            frame.StartDelaySeconds,
            duration,
            frame.Adc,
            parameters.Gate,
            channelNumbers,
            channels,
            sampleTimes);
    }

    /// <summary>
    /// Peak-preserving decimation: each block of samples becomes its minimum and maximum, in time
    /// order. Plain down-sampling would hide single-sample defect echoes, so it is not used (05 §7).
    /// </summary>
    private static (short[] Samples, double[]? Times) Resample(
        ReadOnlySpan<short> samples,
        double sampleRateHz,
        double startDelaySeconds,
        int? maxPoints)
    {
        if (maxPoints is null || samples.Length <= maxPoints.Value)
        {
            return (samples.ToArray(), null);
        }

        int target = Math.Max(2, maxPoints.Value);
        int blocks = Math.Max(1, target / 2);
        int blockSize = (int)Math.Ceiling(samples.Length / (double)blocks);
        var output = new List<short>(blocks * 2);
        var times = new List<double>(blocks * 2);
        for (int start = 0; start < samples.Length; start += blockSize)
        {
            int end = Math.Min(start + blockSize, samples.Length);
            int minIndex = start;
            int maxIndex = start;
            short min = samples[start];
            short max = samples[start];
            for (int index = start + 1; index < end; index++)
            {
                short value = samples[index];
                if (value < min)
                {
                    min = value;
                    minIndex = index;
                }

                if (value > max)
                {
                    max = value;
                    maxIndex = index;
                }
            }

            bool minFirst = minIndex <= maxIndex;
            output.Add(minFirst ? min : max);
            times.Add(TimeOf(minFirst ? minIndex : maxIndex, sampleRateHz, startDelaySeconds));
            output.Add(minFirst ? max : min);
            times.Add(TimeOf(minFirst ? maxIndex : minIndex, sampleRateHz, startDelaySeconds));
        }

        return (output.ToArray(), times.ToArray());
    }

    private static double[] BuildLinearTimes(int count, double sampleRateHz, double startDelaySeconds)
    {
        var times = new double[count];
        for (int index = 0; index < count; index++)
        {
            times[index] = TimeOf(index, sampleRateHz, startDelaySeconds);
        }

        return times;
    }

    private static double TimeOf(int sampleIndex, double sampleRateHz, double startDelaySeconds) =>
        startDelaySeconds + (sampleRateHz > 0 ? sampleIndex / sampleRateHz : 0);
}
