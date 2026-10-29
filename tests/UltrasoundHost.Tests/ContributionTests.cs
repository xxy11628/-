using Microsoft.Extensions.DependencyInjection;
using UltrasoundHost.Contracts;
using UltrasoundHost.Core;

namespace UltrasoundHost.Tests;

/// <summary>Minimal processor for contribution tests; never actually called.</summary>
internal sealed class NoopProcessor : IFrameProcessor
{
    public string Name => "noop";

    public ProcessedFrame Process(AcquisitionFrame frame, AcquisitionParameters parameters) =>
        new(frame.Sequence, frame.ParamVersion, Array.Empty<GateResult>());
}

/// <summary>
/// Plugin that is both a processor and a view contributor and deliberately registers nothing in DI,
/// so these tests prove the shell has to go through <c>PluginHost.GetActive&lt;T&gt;()</c> (rule R11).
/// </summary>
internal sealed class ContributingPlugin : IPlugin, IProcessorContribution, IViewContribution
{
    private readonly bool _throwOnStart;

    public ContributingPlugin(string id, string[]? dependsOn = null, bool throwOnStart = false)
    {
        _throwOnStart = throwOnStart;
        Manifest = new PluginManifest(id, new Version(0, 1, 0), PluginKind.Processing, id)
        {
            DependsOn = dependsOn ?? Array.Empty<string>(),
        };
        Views = new[] { new ViewDescriptor(id + ".view", id + " view", ViewKinds.AScan) };
    }

    public PluginManifest Manifest { get; }

    public IReadOnlyList<ViewDescriptor> Views { get; }

    public IFrameProcessor CreateProcessor() => new NoopProcessor();

    public void Register(IServiceCollection services)
    {
        // Intentionally empty: contributions come from the plugin object, not from DI.
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) =>
        _throwOnStart ? throw new InvalidOperationException("injected start failure") : ValueTask.CompletedTask;
}

public class ContributionTests
{
    private static string[] ViewContributorIds(IEnumerable<IViewContribution> contributions) =>
        contributions.Select(contribution => ((ContributingPlugin)contribution).Manifest.Id).ToArray();

    private static string[] ProcessorContributorIds(IEnumerable<IProcessorContribution> contributions) =>
        contributions.Select(contribution => ((ContributingPlugin)contribution).Manifest.Id).ToArray();

    [Fact]
    public async Task GetActive_returns_running_contributors_in_start_order()
    {
        var plugins = new IPlugin[]
        {
            new ContributingPlugin("c", new[] { "b" }),
            new ContributingPlugin("b", new[] { "a" }),
            new ContributingPlugin("a"),
        };

        await using Session session = await Session.StartAsync(plugins);

        Assert.Equal(new[] { "a", "b", "c" }, ViewContributorIds(session.Host.GetActive<IViewContribution>()));
        Assert.Equal(new[] { "a", "b", "c" }, ProcessorContributorIds(session.Host.GetActive<IProcessorContribution>()));
    }

    [Fact]
    public async Task GetActive_excludes_plugins_that_faulted_in_start()
    {
        var plugins = new IPlugin[]
        {
            new ContributingPlugin("faulty", throwOnStart: true),
            new ContributingPlugin("ok"),
        };

        await using Session session = await Session.StartAsync(plugins);

        Assert.Equal(PluginState.Faulted, session.State("faulty"));
        Assert.Equal(new[] { "ok" }, ViewContributorIds(session.Host.GetActive<IViewContribution>()));
        Assert.Equal(new[] { "ok" }, ProcessorContributorIds(session.Host.GetActive<IProcessorContribution>()));
    }

    [Fact]
    public async Task GetActive_is_empty_before_start_and_throws_before_build()
    {
        var host = new PluginHost(new IPlugin[] { new ContributingPlugin("a") });
        try
        {
            Assert.Throws<InvalidOperationException>(() => host.GetActive<IViewContribution>());

            host.Build(new ServiceCollection());
            Assert.Empty(host.GetActive<IViewContribution>());
        }
        finally
        {
            await host.DisposeAsync();
        }
    }
}
