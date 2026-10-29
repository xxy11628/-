using UltrasoundHost.Contracts;

namespace UltrasoundHost.Application;

/// <summary>
/// Keeps the last N applied parameter versions so a frame can be processed with the parameters it
/// was actually acquired under (05 §6), instead of with whatever happens to be current. Parameters
/// are registered before they are applied to the device, so a frame carrying the new version never
/// arrives before its parameters are known.
/// </summary>
public sealed class ParameterHistory
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<int, AcquisitionParameters> _byVersion = new();
    private readonly Queue<int> _order = new();

    public ParameterHistory(int capacity = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byVersion.Count;
            }
        }
    }

    /// <summary>Registers a version. Re-registering an existing version keeps the latest instance.</summary>
    public void Add(AcquisitionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        lock (_gate)
        {
            if (_byVersion.TryGetValue(parameters.Version, out AcquisitionParameters? existing))
            {
                _byVersion[parameters.Version] = parameters;
                if (ReferenceEquals(existing, parameters))
                {
                    return;
                }

                return;
            }

            _byVersion[parameters.Version] = parameters;
            _order.Enqueue(parameters.Version);
            while (_order.Count > _capacity)
            {
                int evicted = _order.Dequeue();
                _byVersion.Remove(evicted);
            }
        }
    }

    /// <summary>Looks up the parameters a frame with <paramref name="version"/> was acquired under.</summary>
    public bool TryGet(int version, out AcquisitionParameters parameters)
    {
        lock (_gate)
        {
            return _byVersion.TryGetValue(version, out parameters!);
        }
    }

    public IReadOnlyList<int> Versions
    {
        get
        {
            lock (_gate)
            {
                return _order.ToArray();
            }
        }
    }
}
