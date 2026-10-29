using UltrasoundHost.Contracts;
using UltrasoundHost.Plugins.View.Basic;

namespace UltrasoundHost.Tests;

/// <summary>Reusable view contract suite (07 §4.4).</summary>
public abstract class ViewContractTestsBase
{
    protected abstract IReadOnlyList<ViewDescriptor> CreateViews();

    [Fact]
    public void VC1_view_ids_are_unique_and_titles_and_kinds_are_not_empty()
    {
        IReadOnlyList<ViewDescriptor> views = CreateViews();

        Assert.NotEmpty(views);
        Assert.Equal(views.Count, views.Select(view => view.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(views, view =>
        {
            Assert.False(string.IsNullOrWhiteSpace(view.Id));
            Assert.False(string.IsNullOrWhiteSpace(view.Title));
            Assert.False(string.IsNullOrWhiteSpace(view.ViewKind));
        });
    }

    [Fact]
    public void VC2_matches_follows_the_declared_schemes()
    {
        var aScanOnly = new ViewDescriptor("a", "A", ViewKinds.AScan, AcquisitionSchemes.AScan);
        var anyScheme = new ViewDescriptor("b", "B", "Custom");

        Assert.True(aScanOnly.Matches(AcquisitionSchemes.AScan));
        Assert.False(aScanOnly.Matches(AcquisitionSchemes.Fmc));
        Assert.True(anyScheme.Matches(AcquisitionSchemes.Fmc));
    }

    [Fact]
    public void VC3_display_needs_are_valid()
    {
        foreach (ViewDescriptor view in CreateViews())
        {
            Assert.Empty(view.Needs.Validate());
        }
    }
}

public sealed class BasicViewPluginViewContractTests : ViewContractTestsBase
{
    protected override IReadOnlyList<ViewDescriptor> CreateViews() => new BasicViewPlugin().Views;
}
