namespace UltimateUpdater.Models.Settings;

/// <summary>One package's repeated-failure tally (<see cref="AppSettings.PersistentFailures"/>).
/// <see cref="Signature"/> is built from a CLASSIFIED reason (the resource key an
/// <c>InstallErrorExplainer.Explain</c> result carries, plus its numeric arguments), never the raw tool
/// output; raw text carries changing paths/timestamps and would never repeat, so nothing would ever
/// reach the threshold. Only a matching signature increments <see cref="Count"/>; anything else resets it
/// to a fresh record. <see cref="LastErrorText"/> is kept purely for display (the row's existing error-
/// detail path) and plays no part in the comparison.</summary>
public sealed class PersistentFailureRecord
{
    public string Signature { get; set; } = string.Empty;
    public int Count { get; set; }
    public DateTimeOffset LastAt { get; set; }
    public string? LastErrorText { get; set; }
}
