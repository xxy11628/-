using UltrasoundHost.Contracts;

namespace UltrasoundHost.Plugins.Processing.Basic;

/// <summary>
/// Amplitude gate for A-scans. For every channel it finds the largest |sample| inside the gate,
/// reports it as a fraction of full scale, and flags it when it reaches the threshold.
/// </summary>
public sealed class GatePeakProcessor : IFrameProcessor
{
    public string Name => "gate-peak";

    public ProcessedFrame Process(AcquisitionFrame frame, AcquisitionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(parameters);
        if (frame.Kind != FrameKind.AScan || frame.Tx != 1)
        {
            throw new NotSupportedException("the gate processor handles A-scan frames only (Tx must be 1)");
        }

        if (frame.ParamVersion != parameters.Version)
        {
            throw new InvalidOperationException(
                "frame was acquired with parameter version " + frame.ParamVersion + " but version " + parameters.Version + " is current");
        }

        double sampleRate = frame.SampleRateHz;
        if (!(sampleRate > 0))
        {
            throw new InvalidOperationException("the frame carries no sample rate");
        }

        double fullScale = frame.Adc.FullScaleCounts;
        if (!(fullScale > 0))
        {
            throw new InvalidOperationException("the frame carries no valid ADC format");
        }

        double delay = frame.StartDelaySeconds;
        GateConfig gate = parameters.Gate;

        // Gate edges converted to sample indices; the small epsilon keeps exact edges inside the gate.
        double firstD = Math.Max(0.0, Math.Ceiling(((gate.StartSeconds - delay) * sampleRate) - 1e-9));
        double lastD = Math.Min(frame.Samples - 1, Math.Floor(((gate.EndSeconds - delay) * sampleRate) + 1e-9));
        bool hasWindow = firstD <= lastD;
        int first = hasWindow ? (int)firstD : 0;
        int last = hasWindow ? (int)lastD : -1;

        var results = new GateResult[frame.Rx];
        for (int channel = 0; channel < frame.Rx; channel++)
        {
            results[channel] = Evaluate(frame.Span(0, channel), channel, first, last, delay, sampleRate, fullScale, parameters);
        }

        return new ProcessedFrame(frame.Sequence, frame.ParamVersion, results);
    }

    private static GateResult Evaluate(
        ReadOnlySpan<short> samples,
        int channel,
        int first,
        int last,
        double delay,
        double sampleRate,
        double fullScale,
        AcquisitionParameters parameters)
    {
        int peakIndex = -1;
        int peakMagnitude = 0;
        for (int n = first; n <= last; n++)
        {
            int magnitude = Math.Abs((int)samples[n]);
            if (magnitude > peakMagnitude)
            {
                peakMagnitude = magnitude;
                peakIndex = n;
            }
        }

        if (peakIndex < 0)
        {
            return new GateResult(channel, false, 0.0, -1, 0.0, 0.0);
        }

        double amplitude = Math.Min(1.0, peakMagnitude / fullScale);
        double time = delay + (peakIndex / sampleRate);
        double path = parameters.SoundSpeedMps * time / 2.0;
        return new GateResult(channel, amplitude >= parameters.Gate.Threshold, amplitude, peakIndex, time, path);
    }
}
