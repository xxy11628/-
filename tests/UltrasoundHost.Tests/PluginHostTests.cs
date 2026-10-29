using Microsoft.Extensions.DependencyInjection;
using UltrasoundHost.Contracts;
using UltrasoundHost.Core;

namespace UltrasoundHost.Tests;

internal sealed record Marker(string Id);

/// <summary>A scriptable plugin that records every lifecycle call in a shared log.</summary>
internal sealed class FakePlugin : IPlugin, IDisposable
{
    private readonly List<string> _log;

    public FakePlugin(
        string id,
        List<string> log,
        string[]? dependsOn = null,
        PluginKind kind = PluginKind.Processing,
        Func<PluginManifest, PluginManifest>? tweak = null)
    {
        var manifest = new PluginManifest(id, new Version(1, 0, 0), kind, id)
        {
            DependsOn = dependsOn ?? Array.Empty<string>(),
        };
        Manifest = tweak is null ? manifest : tweak(manifest);
        _log = log;
    }

    public PluginManifest Manifest { get; }

    public bool ThrowOnRegister { get; set; }

    public bool ThrowOnInitialize { get; set; }

    public bool ThrowOnStart { get; set; }

    public bool ThrowOnStop { get; set; }

    public void Register(IServiceCollection services)
    {
        _log.Add("register:" + Manifest.Id);
        services.AddSingleton(new Marker(Manifest.Id));
        if (ThrowOnRegister)
        {
            throw new InvalidOperationException("boom");
        }
    }

    public ValueTask InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        _log.Add("init:" + Manifest.Id);
        if (ThrowOnInitialize)
        {
            throw new InvalidOperationException("boom");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        _log.Add("start:" + Manifest.Id);
        if (ThrowOnStart)
        {
            throw new InvalidOperationException("boom");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _log.Add("stop:" + Manifest.Id);
        if (ThrowOnStop)
        {
            throw new InvalidOperationException("boom");
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _log.Add("dispose:" + Manifest.Id);
    }
}

/// <summary>Builds, initialises and starts a host so each test can look at the outcome.</summary>
internal sealed class Session : IAsyncDisposable
{
    private Session(PluginHost host, ServiceProvider provider)
    {
        Host = host;
        Provider = provider;
    }

    public PluginHost Host { get; }

    public ServiceProvider Provider { get; }

    public static async Task<Session> StartAsync(IEnumerable<IPlugin> plugins, HostCapabilities? capabilities = null)
    {
        var services = new ServiceCollection();
        var host = new PluginHost(plugins, capabilities);
        host.Build(services);
        ServiceProvider provider = services.BuildServiceProvider();
        await host.InitializeAsync(provider);
        await host.StartAsync();
        return new Session(host, provider);
    }

    public PluginState State(string id) => Host.Statuses.Single(s => s.Id == id).State;

    public string? Reason(string id) => Host.Statuses.Single(s => s.Id == id).Reason;

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Provider.DisposeAsync();
    }
}

public class PluginHostTests
{
    private static List<string> Only(List<string> log, string prefix) =>
        log.Where(entry => entry.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task Dependencies_decide_the_order_in_every_phase()
    {
        var log = new List<string>();
        var plugins = new IPlugin[]
        {
            new FakePlugin("c", log, new[] { "b" }),
            new FakePlugin("b", log, new[] { "a" }),
            new FakePlugin("a", log),
        };

        await using Session session = await Session.StartAsync(plugins);

        Assert.Equal(new[] { "register:a", "register:b", "register:c" }, Only(log, "register:"));
        Assert.Equal(new[] { "init:a", "init:b", "init:c" }, Only(log, "init:"));
        Assert.Equal(new[] { "start:a", "start:b", "start:c" }, Only(log, "start:"));
        Assert.All(new[] { "a", "b", "c" }, id => Assert.Equal(PluginState.Running, session.State(id)));
    }

    [Fact]
    public async Task Independent_plugins_keep_their_given_order()
    {
        var log = new List<string>();
        var plugins = new IPlugin[] { new FakePlugin("z", log), new FakePlugin("m", log), new FakePlugin("a", log) };

        await using Session session = await Session.StartAsync(plugins);

        Assert.Equal(new[] { "register:z", "register:m", "register:a" }, Only(log, "register:"));
    }

    [Fact]
    public async Task A_missing_dependency_rejects_the_plugin_and_everything_that_depends_on_it()
    {
        var log = new List<string>();
        var plugins = new IPlugin[]
        {
            new FakePlugin("x", log, new[] { "does-not-exist" }),
            new FakePlugin("y", log, new[] { "x" }),
            new FakePlugin("z", log),
        };

        await using Session session = await Session.StartAsync(plugins);

        Assert.Equal(PluginState.Rejected, session.State("x"));
        Assert.Contains("does-not-exist", session.Reason("x"));
        Assert.Equal(PluginState.Rejected, session.State("y"));
        Assert.Equal(PluginState.Running, session.State("z"));
        Assert.DoesNotContain("register:x", log);
        Assert.DoesNotContain("register:y", log);
    }

    [Fact]
    public async Task A_dependency_cycle_rejects_its_members_and_dependants_but_nothing_else()
    {
        var log = new List<string>();
        var plugins = new IPlugin[]
        {
            new FakePlugin("a", log, new[] { "b" }),
            new FakePlugin("b", log, new[] { "a" }),
            new FakePlugin("d", log, new[] { "a" }),
            new FakePlugin("c", log),
        };

        await using Session session = await Session.StartAsync(plugins);

        foreach (string id in new[] { "a", "b", "d" })
        {
            Assert.Equal(PluginState.Rejected, session.State(id));
            Assert.Contains("circular", session.Reason(id));
        }

        Assert.Equal(PluginState.Running, session.State("c"));
        Assert.Equal(new[] { "register:c" }, Only(log, "register:"));
    }

    [Fact]
    public async Task A_plugin_depending_on_itself_is_a_cycle()
    {
        var log = new List<string>();
        await using Session session = await Session.StartAsync(new IPlugin[] { new FakePlugin("a", log, new[] { "a" }) });
        Assert.Equal(PluginState.Rejected, session.State("a"));
    }

    [Fact]
    public async Task A_plugin_needing_a_newer_contracts_version_is_rejected()
    {
        var log = new List<string>();
        Version newer = new(ContractsInfo.Current.Major, ContractsInfo.Current.Minor + 1, 0);
        var plugin = new FakePlugin("old", log, tweak: m => m with { MinContractsVersion = newer });

        await using Session session = await Session.StartAsync(new IPlugin[] { plugin });

        Assert.Equal(PluginState.Rejected, session.State("old"));
        Assert.Contains("contracts", session.Reason("old"));
        Assert.Empty(log);
    }

    [Fact]
    public async Task A_plugin_needing_a_different_contracts_major_version_is_rejected()
    {
        var log = new List<string>();
        Version otherMajor = new(ContractsInfo.Current.Major + 1, 0, 0);
        var plugin = new FakePlugin("old", log, tweak: m => m with { MinContractsVersion = otherMajor });

        await using Session session = await Session.StartAsync(new IPlugin[] { plugin });

        Assert.Equal(PluginState.Rejected, session.State("old"));
        Assert.Contains("contracts", session.Reason("old"));
        Assert.Empty(log);
    }

    [Fact]
    public async Task Host_requirements_are_checked_before_any_plugin_code_runs()
    {
        var log = new List<string>();
        var needsGpu = new FakePlugin("gpu", log, tweak: m => m with
        {
            HostRequires = new HostRequirements { GpuMemoryBytes = 1L << 30, Tags = new[] { "gpu.cuda" } },
        });
        var plain = new FakePlugin("plain", log);

        await using (Session withoutGpu = await Session.StartAsync(new IPlugin[] { needsGpu, plain }))
        {
            Assert.Equal(PluginState.Rejected, withoutGpu.State("gpu"));
            Assert.Equal(PluginState.Running, withoutGpu.State("plain"));
        }

        var log2 = new List<string>();
        var needsGpu2 = new FakePlugin("gpu", log2, tweak: m => m with
        {
            HostRequires = new HostRequirements { GpuMemoryBytes = 1L << 30, Tags = new[] { "gpu.cuda" } },
        });
        var gpuHost = new HostCapabilities { GpuMemoryBytes = 8L << 30, Tags = new[] { "GPU.CUDA" } };

        await using Session withGpu = await Session.StartAsync(new IPlugin[] { needsGpu2 }, gpuHost);
        Assert.Equal(PluginState.Running, withGpu.State("gpu"));
    }

    [Fact]
    public async Task A_plugin_that_throws_in_Register_is_faulted_and_leaves_no_services_behind()
    {
        var log = new List<string>();
        var plugins = new IPlugin[]
        {
            new FakePlugin("a", log) { ThrowOnRegister = true },
            new FakePlugin("b", log, new[] { "a" }),
            new FakePlugin("c", log),
        };

        await using Session session = await Session.StartAsync(plugins);

        Assert.Equal(PluginState.Faulted, session.State("a"));
        Assert.Contains("Register", session.Reason("a"));
        Assert.Equal(PluginState.Rejected, session.State("b"));
        Assert.Equal(PluginState.Running, session.State("c"));

        // Only c's services reached the container: a's partial registration was discarded.
        Assert.Equal(new[] { "c" }, session.Provider.GetServices<Marker>().Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task A_plugin_that_throws_in_Initialize_is_faulted_and_its_dependants_never_start()
    {
        var log = new List<string>();
        var plugins = new IPlugin[]
        {
            new FakePlugin("a", log) { ThrowOnInitialize = true },
            new FakePlugin("b", log, new[] { "a" }),
            new FakePlugin("c", log),
        };

        await using Session session = await Session.StartAsync(plugins);

        Assert.Equal(PluginState.Faulted, session.State("a"));
        Assert.Equal(PluginState.Rejected, session.State("b"));
        Assert.Contains("'a'", session.Reason("b"));
        Assert.Equal(PluginState.Running, session.State("c"));
        Assert.DoesNotContain("init:b", log);
        Assert.DoesNotContain("start:b", log);
        Assert.DoesNotContain("start:a", log);
    }

    [Fact]
    public async Task A_plugin_that_throws_in_Start_is_faulted_and_the_others_still_start()
    {
        var log = new List<string>();
        var plugins = new IPlugin[]
        {
            new FakePlugin("a", log) { ThrowOnStart = true },
            new FakePlugin("b", log),
        };

        await using Session session = await Session.StartAsync(plugins);

        Assert.Equal(PluginState.Faulted, session.State("a"));
        Assert.Equal(PluginState.Running, session.State("b"));
    }

    [Fact]
    public async Task Stop_runs_in_reverse_order_and_one_failure_does_not_block_the_rest()
    {
        var log = new List<string>();
        var plugins = new IPlugin[]
        {
            new FakePlugin("a", log),
            new FakePlugin("b", log, new[] { "a" }) { ThrowOnStop = true },
            new FakePlugin("c", log, new[] { "b" }),
        };

        await using Session session = await Session.StartAsync(plugins);
        await session.Host.StopAsync();

        Assert.Equal(new[] { "stop:c", "stop:b", "stop:a" }, Only(log, "stop:"));
        Assert.Equal(PluginState.Stopped, session.State("c"));
        Assert.Equal(PluginState.Faulted, session.State("b"));
        Assert.Equal(PluginState.Stopped, session.State("a"));
    }

    [Fact]
    public async Task Dispose_stops_what_is_running_then_disposes_in_reverse_order()
    {
        var log = new List<string>();
        var plugins = new IPlugin[]
        {
            new FakePlugin("a", log),
            new FakePlugin("b", log, new[] { "a" }),
        };

        Session session = await Session.StartAsync(plugins);
        await session.DisposeAsync();

        Assert.Equal(new[] { "stop:b", "stop:a", "dispose:b", "dispose:a" }, log.Skip(log.IndexOf("start:b") + 1).ToArray());
    }

    [Fact]
    public void Duplicate_ids_are_a_wiring_error()
    {
        var log = new List<string>();
        Assert.Throws<InvalidOperationException>(() =>
            new PluginHost(new IPlugin[] { new FakePlugin("a", log), new FakePlugin("a", log) }));
    }

    [Fact]
    public void Build_can_only_run_once_and_must_run_first()
    {
        var log = new List<string>();
        var host = new PluginHost(new IPlugin[] { new FakePlugin("a", log) });

        Assert.Throws<InvalidOperationException>(() => host.CheckSession(new DeviceCapabilities()));

        host.Build(new ServiceCollection());
        Assert.Throws<InvalidOperationException>(() => host.Build(new ServiceCollection()));
    }

    [Fact]
    public async Task Session_check_reports_which_plugins_the_connected_device_cannot_serve()
    {
        var log = new List<string>();
        var needsArray = new FakePlugin("needs-array", log, tweak: m => m with
        {
            DeviceNeeds = new DeviceRequirements { ElementCount = new IntRange(16, 4096) },
        });
        var anything = new FakePlugin("anything", log);
        var device = new FakePlugin("dev", log, kind: PluginKind.Device);

        await using Session session = await Session.StartAsync(new IPlugin[] { needsArray, anything, device });

        SessionCheckResult single = session.Host.CheckSession(new DeviceCapabilities { ElementCount = IntRange.Exactly(1) });
        Assert.False(single.AllSatisfied);
        Assert.DoesNotContain(single.Items, i => i.PluginId == "dev");
        SessionCheckItem failed = Assert.Single(single.Items, i => !i.Satisfied);
        Assert.Equal("needs-array", failed.PluginId);
        Assert.Contains(failed.Unmet, reason => reason.Contains("element count"));

        SessionCheckResult array = session.Host.CheckSession(new DeviceCapabilities { ElementCount = new IntRange(32, 128) });
        Assert.True(array.AllSatisfied);
    }

    [Fact]
    public async Task Session_check_skips_plugins_that_are_not_active()
    {
        var log = new List<string>();
        var broken = new FakePlugin("broken", log, tweak: m => m with
        {
            DeviceNeeds = new DeviceRequirements { ElementCount = new IntRange(500, 600) },
        })
        { ThrowOnInitialize = true };

        await using Session session = await Session.StartAsync(new IPlugin[] { broken, new FakePlugin("fine", log) });

        SessionCheckResult result = session.Host.CheckSession(new DeviceCapabilities());
        Assert.True(result.AllSatisfied);
        Assert.DoesNotContain(result.Items, i => i.PluginId == "broken");
    }

    [Fact]
    public async Task Providers_are_found_by_what_a_device_plugin_declares_it_can_do()
    {
        var log = new List<string>();
        var fmcBoard = new FakePlugin("board.fmc", log, kind: PluginKind.Device, tweak: m => m with
        {
            DeviceProvides = new DeviceCapabilities
            {
                ElementCount = new IntRange(1, 4096),
                ReceiveChannels = new IntRange(1, 4096),
                Schemes = AcquisitionSchemes.AScan | AcquisitionSchemes.Fmc,
            },
        });
        var aScanBoard = new FakePlugin("board.single", log, kind: PluginKind.Device, tweak: m => m with
        {
            DeviceProvides = new DeviceCapabilities { Schemes = AcquisitionSchemes.AScan },
        });
        var noDeclaration = new FakePlugin("board.undeclared", log, kind: PluginKind.Device);

        await using Session session = await Session.StartAsync(new IPlugin[] { fmcBoard, aScanBoard, noDeclaration });

        var wantsFmc128 = new DeviceRequirements { Schemes = AcquisitionSchemes.Fmc, ElementCount = IntRange.Exactly(128) };
        Assert.Equal(new[] { "board.fmc" }, session.Host.FindProviders(wantsFmc128).ToArray());

        var wantsSync = new DeviceRequirements { NeedsMultiBoardSync = true };
        Assert.Empty(session.Host.FindProviders(wantsSync));

        Assert.Equal(new[] { "board.fmc", "board.single" }, session.Host.FindProviders(new DeviceRequirements { Schemes = AcquisitionSchemes.AScan }).ToArray());
    }
}
