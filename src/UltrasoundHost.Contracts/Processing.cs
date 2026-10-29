namespace UltrasoundHost.Contracts;

/// <summary>Result of the amplitude gate for one channel.</summary>
/// <param name="Channel">Receive channel index.</param>
/// <param name="Triggered">Peak inside the gate reached the threshold.</param>
/// <param name="PeakAmplitude">Peak magnitude as a fraction of full scale (0..1).</param>
/// <param name="PeakIndex">Sample index of the peak, or -1 if the gate contained no samples.</param>
/// <param name="PeakTimeSeconds">Time of the peak measured from the pulse (0 if there is no peak).</param>
/// <param name="SoundPathMeters">Sound speed x time / 2 (0 if there is no peak).</param>
public sealed record GateResult(
    int Channel,
    bool Triggered,
    double PeakAmplitude,
    int PeakIndex,
    double PeakTimeSeconds,
    double SoundPathMeters);

public sealed record ProcessedFrame(long Sequence, int ParamVersion, IReadOnlyList<GateResult> Gates);

/// <summary>
/// A processing step. Implementations must not keep a reference to the frame after returning: the
/// caller still owns it. A frame whose <see cref="AcquisitionFrame.ParamVersion"/> differs from the
/// supplied parameters is a caller bug and must be rejected with <see cref="InvalidOperationException"/>.
/// </summary>
public interface IFrameProcessor
{
    string Name { get; }

    ProcessedFrame Process(AcquisitionFrame frame, AcquisitionParameters parameters);
}

/// <summary>
/// A processing plugin's contribution. Collected through
/// <c>PluginHost.GetActive&lt;IProcessorContribution&gt;()</c>, so a processor whose plugin is not
/// Running is never used by the application.
/// </summary>
public interface IProcessorContribution
{
    IFrameProcessor CreateProcessor();
}
