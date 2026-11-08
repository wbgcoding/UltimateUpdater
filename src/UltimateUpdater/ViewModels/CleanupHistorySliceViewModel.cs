using System.Globalization;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

/// <summary>What the pie chart needs of one slice; implemented by the view model so the control stays
/// ignorant of cleanup history.</summary>
public interface IPieSlice
{
    /// <summary>Share of the whole circle, 0 to 1.</summary>
    double Fraction { get; }

    /// <summary>Null means "use the chart's own neutral fill" (the lumped-together slice).</summary>
    Brush? Shade { get; }

    string ToolTipText { get; }
}

/// <summary>One slice of the history pie and its legend line.</summary>
public sealed class CleanupHistorySliceViewModel : IPieSlice
{
    /// <summary>Six shades of the accent blue, dark to light, one per named slice.</summary>
    private static readonly Brush[] Shades = new[] { "#004A83", "#005FA8", "#0078D4", "#3A96DD", "#6CB1E6", "#9ECBEF" }
        .Select(hex =>
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return (Brush)brush;
        })
        .ToArray();

    private readonly HistorySliceData _data;

    public CleanupHistorySliceViewModel(HistorySliceData data, int index, long totalBytes)
    {
        _data = data;
        Fraction = totalBytes > 0 ? (double)data.Bytes / totalBytes : 0;
        Shade = data.IsOther ? null : Shades[Math.Min(index, Shades.Length - 1)];
    }

    public double Fraction { get; }

    public Brush? Shade { get; }

    public bool IsOther => _data.IsOther;

    public string Title => _data.TitleKey is { } key
        ? CleanupHistoryAreaViewModel.FormatTitle(key, _data.TitleArguments)
        : LocalizationService.Instance["CleanupHistoryOther"];

    public string FreedText => ByteSize.Format(_data.Bytes);

    /// <summary>Legend value: the whole-number share in the UI language's own percent style.</summary>
    public string PercentText => (_data.Percent / 100.0).ToString("P0", CultureInfo.CurrentUICulture);

    /// <summary>Name, share of everything freed, and how many runs touched the area: the same three lines
    /// on the pie slice, the legend line and as the accessible name.</summary>
    public string ToolTipText
    {
        get
        {
            var loc = LocalizationService.Instance;
            var runs = string.Format(
                loc[_data.RunCount == 1 ? "CleanupHistoryRunCountOne" : "CleanupHistoryRunCount"],
                _data.RunCount, CleanupHistoryRowViewModel.FormatWhen(_data.LastRun));
            return $"{Title}\n{string.Format(loc["CleanupHistorySliceShare"], _data.Percent, FreedText)}\n{runs}";
        }
    }

    public override string ToString() => $"{Title}, {PercentText}";
}
