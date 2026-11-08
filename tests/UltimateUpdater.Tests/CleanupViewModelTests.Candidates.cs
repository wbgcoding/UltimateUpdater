using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    private const long OneGb = 1024L * 1024 * 1024;

    private static PythonVersionCandidateDto PythonCandidate(string name, long bytes) => new()
    {
        PythonExe = $@"C:\Tools\{name}\python.exe",
        DisplayLabel = name,
        SizeBytes = bytes,
    };

    [Fact]
    public void Old_python_row_counts_only_the_ticked_candidates()
    {
        var language = LocalizationService.Instance;
        language.Apply("en");
        var viewModel = Create(CreateSettings());
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.OldPythonVersions);
        item.IsScanning = false;
        item.SetOldPythonVersionCandidates(
            [PythonCandidate("Py38", OneGb), PythonCandidate("Py39", 2 * OneGb), PythonCandidate("Py310", 3 * OneGb)]);
        item.Bytes = 6 * OneGb;
        SelectOnly(viewModel, item);

        Assert.Equal(0, item.SelectedEntryBytes);
        Assert.Equal(0, viewModel.SelectedBytes);

        var rowChanged = new List<string?>();
        item.PropertyChanged += (_, e) => rowChanged.Add(e.PropertyName);
        var totalChanged = new List<string?>();
        viewModel.PropertyChanged += (_, e) => totalChanged.Add(e.PropertyName);

        item.OldPythonVersionCandidates[1].IsSelected = true;

        Assert.Equal(2 * OneGb, item.SelectedEntryBytes);
        Assert.Equal(1, item.SelectedEntryFiles);
        Assert.Equal(2 * OneGb, viewModel.SelectedBytes);
        Assert.Equal(6 * OneGb, viewModel.TotalBytes);
        Assert.Contains(nameof(CleanupItemViewModel.SelectedEntryBytes), rowChanged);
        Assert.Contains(nameof(CleanupItemViewModel.ChosenSizeText), rowChanged);
        Assert.Contains(nameof(CleanupViewModel.SelectedBytes), totalChanged);

        var (headline, _) = viewModel.BuildConfirmationTexts();
        Assert.Contains(ByteSize.Format(2 * OneGb), headline);
        Assert.DoesNotContain(ByteSize.Format(6 * OneGb), headline);

        item.OldPythonVersionCandidates[1].IsSelected = false;
        Assert.Equal(0, item.SelectedEntryBytes);
    }

    [Fact]
    public void Leftover_row_counts_only_the_ticked_candidates()
    {
        // The leftover row is not in the catalogue, so the row view model is built on its own.
        var selectionChanges = 0;
        var target = new CleanupTarget(
            "leftover-test", "CleanupOldPythonVersions", "CleanupOldPythonVersionsDesc",
            CleanupKind.LeftoverInstalls, DefaultEnabled: false, CleanupRiskLevel.Risky, "CleanupOldPythonVersionsConsequence");
        var item = new CleanupItemViewModel(target, () => selectionChanges++) { IsSelected = true };
        item.IsScanning = false;
        item.SetLeftoverCandidates(
        [
            new LeftoverCandidateDto { Path = @"C:\Tools\A", DisplayName = "A", SizeBytes = OneGb },
            new LeftoverCandidateDto { Path = @"C:\Tools\B", DisplayName = "B", SizeBytes = 2 * OneGb },
            new LeftoverCandidateDto { Path = @"C:\Tools\C", DisplayName = "C", SizeBytes = 3 * OneGb },
        ]);
        item.Bytes = 6 * OneGb;
        selectionChanges = 0;

        item.LeftoverCandidates[1].IsSelected = true;

        Assert.Equal(2 * OneGb, item.SelectedEntryBytes);
        Assert.Equal(1, selectionChanges);

        item.SetLeftoverCandidates(null);
        Assert.Equal(0, item.SelectedEntryBytes);
    }
}
