using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UltrasoundHost.Contracts;

namespace UltrasoundHost.Core;

/// <summary>
/// The micro-kernel: orders plugins by their declared dependencies, checks preconditions, drives the
/// lifecycle and keeps a failing plugin from taking the rest down.
/// <para>
/// Usage: <c>Build(services)</c>, build the service provider, then <c>InitializeAsync</c>,
/// <c>StartAsync</c>, later <c>StopAsync</c> / <c>DisposeAsync</c>.
/// </para>
/// <para>
/// Known limitation: services a plugin registered successfully stay in the container even if the
/// plugin later faults. Consumers should check <see cref="Statuses"/> before relying on a plugin.
/// </para>
/// </summary>
public sealed class PluginHost : IAsyncDisposable
{
    private readonly List<Entry> _entries = new();
    private readonly List<Entry> _order = new();
    private readonly HostCapabilities _host;
    private readonly HashSet<string> _hostTags;
    private readonly ILogger _log;
    private bool _built;
    private bool _disposed;

    public PluginHost(IEnumerable<IPlugin> plugins, HostCapabilities? host = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        _host = host ?? new HostCapabilities();
        _hostTags = new HashSet<string>(_host.Tags, StringComparer.OrdinalIgnoreCase);
        _log = logger ?? NullLogger.Instance;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (IPlugin plugin in plugins)
        {
            if (plugin is null)
            {
                throw new ArgumentException("the plugin list contains null", nameof(plugins));
            }

            PluginManifest manifest = plugin.Manifest ?? throw new ArgumentException("a plugin returned a null manifest", nameof(plugins));
            if (string.IsNullOrWhiteSpace(manifest.Id))
            {
                throw new ArgumentException("a plugin has an empty id", nameof(plugins));
            }

            if (!ids.Add(manifest.Id))
            {
                throw new InvalidOperationException("duplicate plugin id '" + manifest.Id + "'");
            }

            _entries.Add(new Entry(plugin, manifest));
        }
    }

    public IReadOnlyList<PluginStatus> Statuses =>
        _entries.Select(e => new PluginStatus(e.Manifest.Id, e.Manifest.DisplayName, e.Manifest.Kind, e.State, e.Reason)).ToList();

    /// <summary>
    /// Checks preconditions, orders plugins by dependency and merges each plugin's services into
    /// <paramref name="services"/>. A plugin that fails is rejected or faulted; the rest carry on.
    /// </summary>
    public void Build(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (_built)
        {
            throw new InvalidOperationException("Build has already been called");
        }

        _built = true;

        // 1. Preconditions that need no plugin code to run.
        foreach (Entry entry in _entries)
        {
            string? reason = CheckPreconditions(entry.Manifest);
            if (reason is not null)
            {
                Reject(entry, reason);
            }
        }

        // 2. Dependencies that are missing or rejected reject their dependants, repeated until stable.
        bool changed;
        do
        {
            changed = false;
            foreach (Entry entry in _entries)
            {
                if (entry.State != PluginState.Discovered)
                {
                    continue;
                }

                foreach (string dependency in entry.Manifest.DependsOn)
                {
                    Entry? target = Find(dependency);
                    if (target is null)
                    {
                        Reject(entry, "dependency '" + dependency + "' is not available");
                        changed = true;
                        break;
                    }

                    if (target.State == PluginState.Rejected)
                    {
                        Reject(entry, "dependency '" + dependency + "' was rejected");
                        changed = true;
                        break;
                    }
                }
            }
        }
        while (changed);

        // 3. Stable topological order. Whatever cannot be placed is part of, or depends on, a cycle.
        var pending = _entries.Where(e => e.State == PluginState.Discovered).ToList();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<Entry>();
        while (pending.Count > 0)
        {
            Entry? next = pending.FirstOrDefault(e => e.Manifest.DependsOn.All(placed.Contains));
            if (next is null)
            {
                break;
            }

            pending.Remove(next);
            ordered.Add(next);
            placed.Add(next.Manifest.Id);
        }

        if (pending.Count > 0)
        {
            string members = string.Join(", ", pending.Select(e => e.Manifest.Id));
            foreach (Entry entry in pending)
            {
                Reject(entry, "part of, or dependent on, a circular dependency among: " + members);
            }
        }

        // 4. Register in order, into a private collection that is merged only on success.
        foreach (Entry entry in ordered)
        {
            string? bad = FindUnusableDependency(entry);
            if (bad is not null)
            {
                Reject(entry, bad);
                continue;
            }

            var temp = new ServiceCollection();
            try
            {
                entry.Plugin.Register(temp);
            }
            catch (Exception ex)
            {
                Fault(entry, "Register", ex);
                continue;
            }

            foreach (ServiceDescriptor descriptor in temp)
            {
                services.Add(descriptor);
            }

            entry.State = PluginState.Registered;
            _order.Add(entry);
        }
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        EnsureBuilt();
        foreach (Entry entry in _order)
        {
            if (entry.State != PluginState.Registered)
            {
                continue;
            }

            string? bad = FindUnusableDependency(entry);
            if (bad is not null)
            {
                Reject(entry, bad);
                continue;
            }

            try
            {
                await entry.Plugin.InitializeAsync(services, cancellationToken).ConfigureAwait(false);
                entry.State = PluginState.Initialized;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Fault(entry, "Initialize", ex);
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureBuilt();
        foreach (Entry entry in _order)
        {
            if (entry.State != PluginState.Initialized)
            {
                continue;
            }

            string? bad = FindUnusableDependency(entry);
            if (bad is not null)
            {
                Reject(entry, bad);
                continue;
            }

            try
            {
                await entry.Plugin.StartAsync(cancellationToken).ConfigureAwait(false);
                entry.State = PluginState.Running;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Fault(entry, "Start", ex);
            }
        }
    }

    /// <summary>Stops running plugins in reverse start order. A plugin that throws is marked faulted; the others still stop.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        EnsureBuilt();
        for (int i = _order.Count - 1; i >= 0; i--)
        {
            Entry entry = _order[i];
            if (entry.State != PluginState.Running)
            {
                continue;
            }

            try
            {
                await entry.Plugin.StopAsync(cancellationToken).ConfigureAwait(false);
                entry.State = PluginState.Stopped;
            }
            catch (Exception ex)
            {
                Fault(entry, "Stop", ex);
            }
        }
    }

    /// <summary>
    /// Matches every active non-device plugin's <see cref="PluginManifest.DeviceNeeds"/> against the
    /// capabilities the connected device reported, so unsupported combinations are refused before acquisition starts.
    /// </summary>
    public SessionCheckResult CheckSession(DeviceCapabilities device)
    {
        ArgumentNullException.ThrowIfNull(device);
        EnsureBuilt();
        var items = new List<SessionCheckItem>();
        foreach (Entry entry in _order)
        {
            if (entry.Manifest.Kind == PluginKind.Device || IsDead(entry))
            {
                continue;
            }

            IReadOnlyList<string> unmet = CapabilityMatcher.Match(entry.Manifest.DeviceNeeds, device);
            items.Add(new SessionCheckItem(entry.Manifest.Id, unmet.Count == 0, unmet));
        }

        return new SessionCheckResult(items);
    }

    /// <summary>Ids of active device plugins whose declared ceiling satisfies <paramref name="needs"/>.</summary>
    public IReadOnlyList<string> FindProviders(DeviceRequirements needs)
    {
        ArgumentNullException.ThrowIfNull(needs);
        EnsureBuilt();
        var result = new List<string>();
        foreach (Entry entry in _order)
        {
            if (entry.Manifest.Kind != PluginKind.Device || IsDead(entry) || entry.Manifest.DeviceProvides is null)
            {
                continue;
            }

            if (CapabilityMatcher.Match(needs, entry.Manifest.DeviceProvides).Count == 0)
            {
                result.Add(entry.Manifest.Id);
            }
        }

        return result;
    }

    /// <summary>
    /// Plugins in the given contribution role that are currently Running, in dependency (start) order.
    /// Shells and Application code must enumerate plugin capabilities through this method (rule R11),
    /// never by resolving contribution interfaces from the DI container. A plugin that is Rejected,
    /// Faulted or not started yet simply does not appear.
    /// </summary>
    public IReadOnlyList<T> GetActive<T>()
    {
        EnsureBuilt();
        var active = new List<T>();
        foreach (Entry entry in _order)
        {
            if (entry.State == PluginState.Running && entry.Plugin is T contribution)
            {
                active.Add(contribution);
            }
        }

        return active;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_built)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var disposeOrder = new List<Entry>();
        for (int i = _order.Count - 1; i >= 0; i--)
        {
            disposeOrder.Add(_order[i]);
        }

        disposeOrder.AddRange(_entries.Where(e => !_order.Contains(e)));
        foreach (Entry entry in disposeOrder)
        {
            try
            {
                if (entry.Plugin is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (entry.Plugin is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Plugin {PluginId} threw while being disposed", entry.Manifest.Id);
            }
        }
    }

    private static bool IsDead(Entry entry) => entry.State is PluginState.Rejected or PluginState.Faulted;

    private string? CheckPreconditions(PluginManifest manifest)
    {
        Version current = ContractsInfo.Current;
        if (manifest.MinContractsVersion.Major != current.Major)
        {
            return "needs contracts " + manifest.MinContractsVersion + " but the host provides " + current + " (different major version)";
        }

        if (manifest.MinContractsVersion > current)
        {
            return "needs contracts >= " + manifest.MinContractsVersion + " but the host provides " + current;
        }

        HostRequirements required = manifest.HostRequires;
        if (required.GpuMemoryBytes > _host.GpuMemoryBytes)
        {
            return "needs " + required.GpuMemoryBytes + " bytes of GPU memory but the host offers " + _host.GpuMemoryBytes;
        }

        foreach (string tag in required.Tags)
        {
            if (!_hostTags.Contains(tag))
            {
                return "needs host feature '" + tag + "' which is not available";
            }
        }

        return null;
    }

    private string? FindUnusableDependency(Entry entry)
    {
        foreach (string dependency in entry.Manifest.DependsOn)
        {
            Entry? target = Find(dependency);
            if (target is null)
            {
                return "dependency '" + dependency + "' is not available";
            }

            if (IsDead(target))
            {
                return "dependency '" + dependency + "' is " + target.State;
            }
        }

        return null;
    }

    private Entry? Find(string id) => _entries.FirstOrDefault(e => string.Equals(e.Manifest.Id, id, StringComparison.Ordinal));

    private void Reject(Entry entry, string reason)
    {
        entry.State = PluginState.Rejected;
        entry.Reason = reason;
        _log.LogWarning("Plugin {PluginId} rejected: {Reason}", entry.Manifest.Id, reason);
    }

    private void Fault(Entry entry, string phase, Exception ex)
    {
        entry.State = PluginState.Faulted;
        entry.Reason = phase + " failed: " + ex.GetType().Name + ": " + ex.Message;
        _log.LogError(ex, "Plugin {PluginId} faulted during {Phase}", entry.Manifest.Id, phase);
    }

    private void EnsureBuilt()
    {
        if (!_built)
        {
            throw new InvalidOperationException("Call Build first");
        }
    }

    private sealed class Entry
    {
        public Entry(IPlugin plugin, PluginManifest manifest)
        {
            Plugin = plugin;
            Manifest = manifest;
        }

        public IPlugin Plugin { get; }

        public PluginManifest Manifest { get; }

        public PluginState State { get; set; } = PluginState.Discovered;

        public string? Reason { get; set; }
    }
}
