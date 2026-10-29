using Microsoft.Extensions.DependencyInjection;

namespace UltrasoundHost.Contracts;

public enum PluginKind
{
    Device,
    Technique,
    Processing,
    View,
    Storage,
}

/// <summary>Everything the host needs to know about a plugin before running any of its code.</summary>
public sealed record PluginManifest(string Id, Version Version, PluginKind Kind, string DisplayName)
{
    /// <summary>Minimum contracts version required. A different major version is always rejected.</summary>
    public Version MinContractsVersion { get; init; } = ContractsInfo.Current;

    /// <summary>Ids of plugins that must be active first. Cycles are forbidden.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();

    public HostRequirements HostRequires { get; init; } = HostRequirements.None;

    /// <summary>Checked against the device's reported capabilities when a session is set up.</summary>
    public DeviceRequirements DeviceNeeds { get; init; } = DeviceRequirements.None;

    /// <summary>For device plugins: the ceiling of what devices from this plugin can do.</summary>
    public DeviceCapabilities? DeviceProvides { get; init; }
}

/// <summary>
/// A unit of functionality. Plugins reference only the Contracts assembly and talk to each
/// other through interfaces defined here, resolved from the service provider.
/// </summary>
public interface IPlugin
{
    PluginManifest Manifest { get; }

    /// <summary>
    /// Declare services. The host passes a private collection and merges it only if this call
    /// succeeds, so a failing plugin leaves nothing behind.
    /// </summary>
    void Register(IServiceCollection services);

    ValueTask InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
