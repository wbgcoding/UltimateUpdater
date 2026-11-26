#if DEBUG
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace UltimateUpdater.Review;

/// <summary>Walks surface x size x language x theme, shows each window, shoots it and its popups, and audits the layout.</summary>
internal static class ReviewRunner
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Resolves selectors (<c>all</c>, <c>area:NAME</c>, an id, or <c>prefix*</c>) to surfaces, in catalog order.</summary>
    public static List<ReviewSurface> Select(IReadOnlyList<string> selectors, out string? error)
    {
        error = null;
        var chosen = new List<ReviewSurface>();
        foreach (var selector in selectors)
        {
            var found = selector.ToLowerInvariant() switch
            {
                "all" => ReviewCatalog.All.ToList(),
                var s when s.StartsWith("area:", StringComparison.Ordinal) =>
                    ReviewCatalog.All.Where(x => x.Area == s["area:".Length..]).ToList(),
                var s when s.EndsWith('*') =>
                    ReviewCatalog.All.Where(x => x.Id.StartsWith(s[..^1], StringComparison.Ordinal)).ToList(),
                var s => ReviewCatalog.All.Where(x => x.Id == s).ToList(),
            };

            if (found.Count == 0)
            {
                error = $"No surface matches '{selector}'. Run --review ids for the list.";
                return [];
            }

            foreach (var surface in found.Where(s => !chosen.Contains(s)))
                chosen.Add(surface);
        }

        return chosen;
    }

    public static string IdList() =>
        string.Join(Environment.NewLine, ReviewCatalog.All.Select(s => $"{s.Id}\t{s.Area}\t{s.Description}")) + Environment.NewLine;

    public static async Task<int> RunAsync(Application app, ReviewOptions options, string scratchRoot, List<string> problems)
    {
        var surfaces = Select(options.Selectors, out var error);
        if (error is not null)
        {
            problems.Add(error);
            return 2;
        }

        Directory.CreateDirectory(options.OutDirectory);
        var findings = new List<string>();
        var info = new List<string>();
        var shots = new List<string>();
        var runs = 0;

        foreach (var language in options.Languages)
        {
            LocalizationService.Instance.Apply(language);
            foreach (var theme in options.Themes)
            {
                ApplyTheme(theme);
                foreach (var surface in surfaces)
                {
                    foreach (var size in options.Sizes)
                    {
                        var tag = $"{surface.Id} {SizeName(size)} {language} {theme}";
                        runs++;
                        try
                        {
                            using var session = new ReviewSession(
                                Path.Combine(scratchRoot, $"s{runs}"), language, theme);
                            Directory.CreateDirectory(session.Root);
                            await RunOneAsync(surface, session, size, options, tag, findings, info, shots);
                        }
                        catch (Exception ex)
                        {
                            problems.Add($"{tag}: {ex}");
                        }
                    }
                }
            }
        }

        WriteAudit(options, findings, info, shots, problems, runs);
        return 0;
    }

    private static string SizeName(ReviewSize size) => size == ReviewSize.Min ? "min" : "default";

    /// <summary>Same sequence the render tests use, so the harness and the tests agree on what a theme looks like.</summary>
    private static void ApplyTheme(string name)
    {
        var theme = name == "dark" ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ThemeService.RemoveHighContrastSurfaceOverrides();
        ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: true);
        ThemeService.ApplyAccentContrast(ApplicationAccentColorManager.SystemAccent);
        if (theme == ApplicationTheme.Dark)
            ThemeService.ApplyDarkPaletteOverrides();
        else
            ThemeService.RemoveDarkPaletteOverrides();
    }

    private static async Task RunOneAsync(
        ReviewSurface surface, ReviewSession session, ReviewSize size, ReviewOptions options, string tag,
        List<string> findings, List<string> info, List<string> shots)
    {
        session.ThemeService.Apply(session.Theme);
        var scene = await surface.Build(session);
        var window = scene.Window;
        try
        {
            if (!Place(window, size))
                return;

            window.Show();
            await ReviewPump.SettleAsync();

            if (scene.AfterShown is { } after)
            {
                await after();
                await ReviewPump.SettleAsync();
            }

            window.UpdateLayout();
            var suffix = $"{SizeName(size)}.{session.Language}.{session.Theme}";
            var fallback = ReviewCapture.FallbackBackground(window);

            var windowFile = Path.Combine(options.OutDirectory, $"{surface.Id}.{suffix}.png");
            ReviewCapture.Save(window, windowFile, fallback);
            shots.Add(windowFile);
            Record(ReviewAudit.Run(window, scene, isPopup: false), tag, findings, info);

            foreach (var (name, element) in scene.Popups)
            {
                if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
                {
                    findings.Add($"{tag} | NOT-SHOWN | popup {name} | has no size, so it did not open");
                    continue;
                }

                var popupFile = Path.Combine(options.OutDirectory, $"{surface.Id}.{name}.{suffix}.png");
                ReviewCapture.Save(element, popupFile, fallback);
                shots.Add(popupFile);
                Record(ReviewAudit.Run(element, scene, isPopup: true), $"{tag} [{name}]", findings, info);
            }
        }
        finally
        {
            if (scene.RunCleanups() is { } failure)
                info.Add($"{tag} | CLEANUP | {failure.GetType().Name}: {failure.Message}");

            CloseForReal(window);
            await ReviewPump.SettleAsync(120);
        }
    }

    /// <summary>Opens a window where it can be shot without taking focus, at the size asked for. False when the size does not apply to it.</summary>
    private static bool Place(Window window, ReviewSize size)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 0;
        window.Top = 0;

        if (size == ReviewSize.Default)
            return true;

        if (window.SizeToContent == SizeToContent.WidthAndHeight || window.MinWidth <= 0)
            return false;

        window.Width = window.MinWidth;
        if (window.SizeToContent == SizeToContent.Manual)
            window.Height = window.MinHeight;

        return true;
    }

    /// <summary>The main window hides to the tray unless the app is exiting, so the flag is flipped around the close.</summary>
    private static void CloseForReal(Window window)
    {
        var app = Application.Current;
        var flag = typeof(App).GetField("_isExiting", BindingFlags.Instance | BindingFlags.NonPublic);
        flag?.SetValue(app, true);
        try
        {
            window.Close();
        }
        finally
        {
            flag?.SetValue(app, false);
        }
    }

    private static void Record((List<ReviewFinding> Findings, List<ReviewFinding> Info) result, string tag, List<string> findings, List<string> info)
    {
        findings.AddRange(result.Findings.Select(f => $"{tag} | {f.Kind} | {f.Element} | {f.Detail}"));
        info.AddRange(result.Info.Select(f => $"{tag} | {f.Kind} | {f.Element} | {f.Detail}"));
    }

    private static void WriteAudit(
        ReviewOptions options, List<string> findings, List<string> info, List<string> shots, List<string> problems, int runs)
    {
        var summary = findings
            .Select(line => line.Split(" | ")[1])
            .GroupBy(kind => kind)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}");

        var audit = new StringBuilder();
        audit.AppendLine($"# audit: {findings.Count} findings in {runs} runs ({string.Join(", ", summary)})");
        audit.AppendLine("# surface size language theme | kind | element | detail");
        foreach (var line in findings.OrderBy(l => l.Split(" | ")[1], StringComparer.Ordinal).ThenBy(l => l, StringComparer.Ordinal))
            audit.AppendLine(line);

        File.WriteAllText(Path.Combine(options.OutDirectory, "audit.txt"), audit.ToString(), Utf8);
        File.WriteAllText(
            Path.Combine(options.OutDirectory, "audit-info.txt"),
            "# text shortened with an ellipsis on purpose, and cleanup notes" + Environment.NewLine
            + string.Join(Environment.NewLine, info.Order(StringComparer.Ordinal)) + Environment.NewLine,
            Utf8);

        var log = new StringBuilder();
        log.AppendLine($"runs={runs} shots={shots.Count} problems={problems.Count}");
        foreach (var problem in problems)
            log.AppendLine("PROBLEM " + problem);

        foreach (var shot in shots)
            log.AppendLine($"{Path.GetFileName(shot)}\t{(File.Exists(shot) ? new FileInfo(shot).Length : -1)}");

        File.WriteAllText(Path.Combine(options.OutDirectory, "review.log"), log.ToString(), Utf8);
    }
}
#endif
