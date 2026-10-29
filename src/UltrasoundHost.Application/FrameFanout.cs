using System.Threading.Channels;
using UltrasoundHost.Contracts;

namespace UltrasoundHost.Application;

/// <summary>Mutable counters behind <see cref="LossCounters"/>; all updates are interlocked.</summary>
internal sealed class LossCountersBuilder
{
    private static readonly FramePathKind[] Kinds = { FramePathKind.Inspection, FramePathKind.Record, FramePathKind.Display };

    private readonly long[] _offered = new long[Kinds.Length];
    private readonly long[] _processed = new long[Kinds.Length];
    private readonly long[] _dropped = new long[Kinds.Length];
    private readonly long[] _unknownVersion = new long[Kinds.Length];
    private long _received;
    private long _sequenceGaps;
    private long _deviceOverflow;
    private long _sequenceViolations;

    public void AddReceived() => Interlocked.Increment(ref _received);

    public void AddSequenceGaps(long frames) => Interlocked.Add(ref _sequenceGaps, frames);

    public void AddDeviceOverflow() => Interlocked.Increment(ref _deviceOverflow);

    public void AddSequenceViolation() => Interlocked.Increment(ref _sequenceViolations);

    public void AddOffered(FramePathKind kind) => Interlocked.Increment(ref _offered[(int)kind]);

    public void AddProcessed(FramePathKind kind) => Interlocked.Increment(ref _processed[(int)kind]);

    public void AddDropped(FramePathKind kind) => Interlocked.Increment(ref _dropped[(int)kind]);

    public void AddUnknownVersion(FramePathKind kind) => Interlocked.Increment(ref _unknownVersion[(int)kind]);

    public LossCounters Snapshot()
    {
        var paths = new Dictionary<FramePathKind, PathCounters>();
        foreach (FramePathKind kind in Kinds)
        {
            int index = (int)kind;
            paths[kind] = new PathCounters(
                Interlocked.Read(ref _offered[index]),
                Interlocked.Read(ref _processed[index]),
                Interlocked.Read(ref _dropped[index]),
                Interlocked.Read(ref _unknownVersion[index]));
        }

        return new LossCounters
        {
            Received = Interlocked.Read(ref _received),
            SequenceGaps = Interlocked.Read(ref _sequenceGaps),
            DeviceOverflow = Interlocked.Read(ref _deviceOverflow),
            SequenceViolations = Interlocked.Read(ref _sequenceViolations),
            Paths = paths,
        };
    }
}

/// <summary>One consumer of the fanout: a bounded queue plus the policy for a full queue.</summary>
internal sealed class FramePath
{
    private readonly Channel<AcquisitionFrame> _channel;

    public FramePath(FramePathKind kind, int capacity, bool dropOldest, LossCountersBuilder counters)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Kind = kind;
        var options = new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = dropOldest ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.Wait,
        };

        _channel = dropOldest
            ? Channel.CreateBounded<AcquisitionFrame>(options, dropped =>
            {
                dropped.Dispose();
                counters.AddDropped(kind);
            })
            : Channel.CreateBounded<AcquisitionFrame>(options);
    }

    public FramePathKind Kind { get; }

    public ChannelReader<AcquisitionFrame> Reader => _channel.Reader;

    public ChannelWriter<AcquisitionFrame> Writer => _channel.Writer;

    public void Complete() => _channel.Writer.TryComplete();
}

/// <summary>
/// The only reader of the device channel. It checks sequence numbers and dispatch flags, then hands
/// every frame to each enabled path (Retain/TryWrite/Dispose, per 05 §3). It never processes samples,
/// so its cost per frame is constant.
/// </summary>
internal sealed class FrameFanout
{
    private readonly ChannelReader<AcquisitionFrame> _source;
    private readonly IReadOnlyList<FramePath> _paths;
    private readonly LossCountersBuilder _counters;
    private readonly Action<Exception>? _onError;
    private long _lastSequence = -1;

    public FrameFanout(
        ChannelReader<AcquisitionFrame> source,
        IReadOnlyList<FramePath> paths,
        LossCountersBuilder counters,
        Action<Exception>? onError = null)
    {
        _source = source;
        _paths = paths;
        _counters = counters;
        _onError = onError;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _source.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_source.TryRead(out AcquisitionFrame? frame))
                {
                    using (frame)
                    {
                        _counters.AddReceived();
                        CheckSequence(frame);

                        if ((frame.Flags & FrameFlags.DeviceOverflow) != 0)
                        {
                            _counters.AddDeviceOverflow();
                        }

                        foreach (FramePath path in _paths)
                        {
                            _counters.AddOffered(path.Kind);

                            // The path gets its own reference; the fanout releases its own below.
                            frame.Retain();
                            if (!path.Writer.TryWrite(frame))
                            {
                                frame.Dispose();
                                _counters.AddDropped(path.Kind);
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
        finally
        {
            foreach (FramePath path in _paths)
            {
                path.Complete();
            }
        }
    }

    private void CheckSequence(AcquisitionFrame frame)
    {
        if (_lastSequence >= 0)
        {
            long expected = _lastSequence + 1;
            if (frame.Sequence > expected)
            {
                _counters.AddSequenceGaps(frame.Sequence - expected);
            }
            else if (frame.Sequence < expected)
            {
                _counters.AddSequenceViolation();
                _onError?.Invoke(new InvalidOperationException(
                    "device sequence " + frame.Sequence + " does not follow " + _lastSequence));
            }
        }

        if (frame.Sequence > _lastSequence)
        {
            _lastSequence = frame.Sequence;
        }
    }
}
