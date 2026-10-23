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
