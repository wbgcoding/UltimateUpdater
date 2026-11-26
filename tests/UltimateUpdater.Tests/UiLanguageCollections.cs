namespace UltimateUpdater.Tests;

/// <summary>
/// Applying a UI language sets the process-wide default culture, so a test class that does it must not run
/// beside any other class that formats a size, percent or date. Classes that touch the language join this
/// collection, which runs alone; the WPF classes that do it are covered by the "wpf" collection below.
/// </summary>
[CollectionDefinition("UiLanguage", DisableParallelization = true)]
public sealed class UiLanguageCollection;

/// <summary>Same reason as above for the WPF test classes that apply a language.</summary>
[CollectionDefinition("wpf", DisableParallelization = true)]
public sealed class WpfCollection;

/// <summary>
/// The window snapshot tests. They build hundreds of full windows on the shared WPF dispatcher and leave the
/// trees behind, which keeps that dispatcher busy for every class that runs after them; the load probes
/// (install progress, accessibility walk) then fail only because of that order. Own collection, ordered
/// last by <see cref="RenderLastCollectionOrderer"/>, so the snapshots cannot disturb any other test.
/// </summary>
[CollectionDefinition(WpfRenderCollection.Name, DisableParallelization = true)]
public sealed class WpfRenderCollection
{
    public const string Name = "wpf-render";
}

/// <summary>Runs every collection in its usual order except the render collection, which goes last.</summary>
public sealed class RenderLastCollectionOrderer : Xunit.ITestCollectionOrderer
{
    public IEnumerable<Xunit.Abstractions.ITestCollection> OrderTestCollections(
        IEnumerable<Xunit.Abstractions.ITestCollection> testCollections)
        => testCollections.OrderBy(c => c.DisplayName.Contains(WpfRenderCollection.Name, StringComparison.Ordinal) ? 1 : 0);
}
