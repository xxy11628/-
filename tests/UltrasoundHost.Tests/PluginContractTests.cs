using Microsoft.Extensions.DependencyInjection;
using UltrasoundHost.Contracts;
using UltrasoundHost.Plugins.Device.SerialUdp;
using UltrasoundHost.Plugins.Device.Simulator;
using UltrasoundHost.Plugins.Processing.Basic;
using UltrasoundHost.Plugins.View.Basic;

namespace UltrasoundHost.Tests;

/// <summary>The plugins that ship with the solution (used for dependency and uniqueness checks).</summary>
internal static class PluginCatalog
{
    public static IReadOnlyList<IPlugin> All { get; } = new IPlugin[]
    {
        new SimulatorPlugin(),
        new SerialUdpPlugin(),
        new GatePeakPlugin(),
        new BasicViewPlugin(),
    };

    public static IReadOnlyList<PluginManifest> Manifests => All.Select(plugin => plugin.Manifest).ToArray();
}

/// <summary>
/// Reusable plugin contract suite (07 §4.1). A new plugin joins by inheriting this class and passing
/// its instance to <see cref="CreatePlugin"/>.
/// </summary>
public abstract class PluginContractTestsBase
{
    protected abstract IPlugin CreatePlugin();

    [Fact]
    public void PG1_manifest_is_complete_and_the_id_matches_the_kind()
    {
        PluginManifest manifest = CreatePlugin().Manifest;

        Assert.False(string.IsNullOrWhiteSpace(manifest.Id));
        Assert.False(string.IsNullOrWhiteSpace(manifest.DisplayName));
        Assert.NotNull(manifest.Version);
        Assert.True(manifest.MinContractsVersion <= ContractsInfo.Current);

        string expectedPrefix = manifest.Kind switch
        {
            PluginKind.Device => "device.",
            PluginKind.Technique => "technique.",
            PluginKind.Processing => "processing.",
            PluginKind.View => "view.",
            PluginKind.Storage => "storage.",
            _ => string.Empty,
        };

        Assert.StartsWith(expectedPrefix, manifest.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void PG2_register_works_on_fresh_collections_without_side_effects()
    {
        IPlugin plugin = CreatePlugin();

        var first = new ServiceCollection();
        plugin.Register(first);
        var second = new ServiceCollection();
        plugin.Register(second);

        Assert.Equal(first.Count, second.Count);
    }

    [Fact]
    public void PG3_dependencies_are_known_and_acyclic()
    {
        var manifests = PluginCatalog.Manifests.ToDictionary(manifest => manifest.Id, StringComparer.Ordinal);

        foreach (PluginManifest manifest in manifests.Values)
        {
            foreach (string dependency in manifest.DependsOn)
            {
                Assert.True(manifests.ContainsKey(dependency), manifest.Id + " depends on unknown plugin " + dependency);
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        bool Visit(string id)
        {
            if (visited.Contains(id))
            {
                return true;
            }

            if (!visiting.Add(id))
            {
                return false;
            }

            foreach (string dependency in manifests[id].DependsOn)
            {
                if (!Visit(dependency))
                {
                    return false;
                }
            }

            visiting.Remove(id);
            visited.Add(id);
            return true;
        }

        foreach (string id in manifests.Keys)
        {
            Assert.True(Visit(id), "dependency cycle through " + id);
        }
    }

    [Fact]
    public async Task PG4_stop_is_safe_before_start()
    {
        await CreatePlugin().StopAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }
}

public sealed class SimulatorPluginContractTests : PluginContractTestsBase
{
    protected override IPlugin CreatePlugin() => new SimulatorPlugin();
}

public sealed class GatePeakPluginContractTests : PluginContractTestsBase
{
    protected override IPlugin CreatePlugin() => new GatePeakPlugin();
}

public sealed class SerialUdpPluginContractTests : PluginContractTestsBase
{
    protected override IPlugin CreatePlugin() => new SerialUdpPlugin();
}

public sealed class BasicViewPluginContractTests : PluginContractTestsBase
{
    protected override IPlugin CreatePlugin() => new BasicViewPlugin();
}
