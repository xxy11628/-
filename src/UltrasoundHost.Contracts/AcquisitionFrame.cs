using System.Buffers;

namespace UltrasoundHost.Contracts;

public enum FrameKind
{
    /// <summary>Pulse-echo records; Tx is 1 and every Rx channel is an independent A-scan.</summary>
    AScan = 0,
    RawChannel = 1,
    BeamformedLine = 2,
    Fmc = 3,
}

/// <summary>Facts about how the frame was acquired that consumers must not guess.</summary>
[Flags]
public enum FrameFlags
{
    None = 0,

    /// <summary>The device-side FIFO overflowed before this frame: data before it was lost.</summary>
    DeviceOverflow = 1,

    /// <summary>The frame's samples are incomplete (for example a short read).</summary>
    Truncated = 2,
}

/// <summary>
/// One block of samples with shape (Tx, Rx, Samples), stored as [tx][rx][sample] in pooled memory.
/// <para>
/// Ownership: <see cref="Rent"/> returns a frame with reference count 1. Whoever reads a frame from a
/// channel owns it and must <see cref="Dispose"/> it. To hand it to a second consumer call
/// <see cref="Retain"/> first. After the last Dispose the buffer returns to the pool and must not be touched.
/// Rented memory is not cleared: the producer has to fill every sample.
/// </para>
/// </summary>
public sealed class AcquisitionFrame : IDisposable
{
    private static long _outstandingFrames;

    private short[]? _buffer;
    private int _refCount;

    /// <summary>Frames currently rented and not yet returned to the pool (07 §5, internal diagnostics).</summary>
    internal static long OutstandingFrames => Interlocked.Read(ref _outstandingFrames);

    private AcquisitionFrame(FrameKind kind, int tx, int rx, int samples, short[] buffer)
    {
        Kind = kind;
        Tx = tx;
        Rx = rx;
        Samples = samples;
        _buffer = buffer;
        _refCount = 1;
    }

    public FrameKind Kind { get; }

    public int Tx { get; }

    public int Rx { get; }

    public int Samples { get; }

    /// <summary>Total number of samples, Tx * Rx * Samples.</summary>
    public int Length => Tx * Rx * Samples;

    /// <summary>
    /// Sequence number assigned by the device (or its protocol layer): strictly increasing with step 1
    /// within one acquisition run of one device. Consumers use it for gap detection (contract 06 §C4).
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>Device-side loss/truncation flags for this frame.</summary>
    public FrameFlags Flags { get; set; } = FrameFlags.None;

    /// <summary>ADC format of the samples in this frame, copied from the device that produced it.</summary>
    public AdcFormat Adc { get; set; } = AdcFormat.Default;

    /// <summary>Device timestamp in nanoseconds.</summary>
    public long TimestampNs { get; set; }

    /// <summary>Version of the <see cref="AcquisitionParameters"/> in effect when this frame was acquired.</summary>
    public int ParamVersion { get; set; }

    public double SampleRateHz { get; set; }

    /// <summary>Time from the pulse to the first sample.</summary>
    public double StartDelaySeconds { get; set; }

    public int RefCount => Volatile.Read(ref _refCount);

    public Memory<short> Data
    {
        get
        {
            short[] buffer = Volatile.Read(ref _buffer) ?? throw new ObjectDisposedException(nameof(AcquisitionFrame));
            return buffer.AsMemory(0, Length);
        }
    }

    public static AcquisitionFrame Rent(FrameKind kind, int tx, int rx, int samples)
    {
        if (tx < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(tx));
        }

        if (rx < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rx));
        }

        if (samples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(samples));
        }

        long total = (long)tx * rx * samples;
        if (total > Array.MaxLength)
        {
            throw new NotSupportedException(
                "A single frame of " + tx + " x " + rx + " x " + samples + " samples does not fit in one buffer. "
                + "Very large matrices (for example 1024-element FMC) need chunked acquisition, which is not defined yet.");
        }

        short[] buffer = ArrayPool<short>.Shared.Rent((int)total);
        Interlocked.Increment(ref _outstandingFrames);
        return new AcquisitionFrame(kind, tx, rx, samples, buffer);
    }

    /// <summary>The samples of one (tx, rx) record.</summary>
    public Span<short> Span(int tx, int rx)
    {
        if ((uint)tx >= (uint)Tx)
        {
            throw new ArgumentOutOfRangeException(nameof(tx));
        }

        if ((uint)rx >= (uint)Rx)
        {
            throw new ArgumentOutOfRangeException(nameof(rx));
        }

        short[] buffer = Volatile.Read(ref _buffer) ?? throw new ObjectDisposedException(nameof(AcquisitionFrame));
        return buffer.AsSpan(((tx * Rx) + rx) * Samples, Samples);
    }

    /// <summary>Adds an owner. Every Retain must be paired with one Dispose.</summary>
    public AcquisitionFrame Retain()
    {
        while (true)
        {
            int current = Volatile.Read(ref _refCount);
            if (current <= 0)
            {
                throw new ObjectDisposedException(nameof(AcquisitionFrame));
            }

            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
            {
                return this;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            short[]? buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                Interlocked.Decrement(ref _outstandingFrames);
                ArrayPool<short>.Shared.Return(buffer);
            }
        }
    }
}
