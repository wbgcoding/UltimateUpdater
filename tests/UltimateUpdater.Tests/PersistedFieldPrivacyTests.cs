using System.Reflection;
using UltimateUpdater.Models;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// Nothing persisted to settings.json or history.json may ever carry a field shaped like a secret or
/// personally identifying data, a property NAME alone can leak intent before any real value is ever
/// written to it, and by the time such a field actually holds something sensitive it is already too
/// late (both files are plain JSON on disk, neither encrypted nor access-restricted beyond the user's
/// own profile). Walks every property reachable from <see cref="AppSettings"/> and
/// <see cref="HistoryEntry"/>, recursing into the app's own nested settings types only, never into
/// framework types (<c>Dictionary</c>, <c>List</c>, <c>DateTimeOffset</c>, ...), which would either loop
/// forever or flag unrelated BCL surface.
/// </summary>
public class PersistedFieldPrivacyTests
{
    private static readonly string[] SuspiciousNameFragments =
    [
        "password", "passwd", "token", "secret", "apikey", "credential", "email", "username", "benutzername",
    ];

    [Fact]
    public void No_persisted_settings_or_history_field_looks_like_a_secret_or_pii()
    {
        var offenders = new List<string>();
        CollectOffenders(typeof(AppSettings), offenders, []);
        CollectOffenders(typeof(HistoryEntry), offenders, []);

        Assert.True(offenders.Count == 0,
            $"field name shaped like a secret/PII: {string.Join(", ", offenders)}");
    }

    private static void CollectOffenders(Type type, List<string> offenders, HashSet<Type> seen)
    {
        if (!seen.Add(type))
            return;

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var lowered = prop.Name.ToLowerInvariant();
            if (SuspiciousNameFragments.Any(lowered.Contains))
                offenders.Add($"{type.Name}.{prop.Name}");

            var propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (propType.Namespace == "UltimateUpdater.Models" || propType == typeof(HistoryEntry))
                CollectOffenders(propType, offenders, seen);
        }
    }
}
