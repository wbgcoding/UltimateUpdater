using System.Globalization;
using System.Text.Json;
using UltimateUpdater.Services.Processes;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>Where the Store packages of the current user come from; a seam so tests need no PowerShell.</summary>
public interface IAppxPackageSource
{
    /// <summary>The removable Store apps, or null when they could not be read.</summary>
    Task<IReadOnlyList<InstalledProgramEntry>?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Where the text of one full `winget list` run comes from; null when winget is absent or failed.</summary>
public interface IWingetListSource
{
    Task<string?> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The Store (MSIX) packages of the current user, from `Get-AppxPackage` run unelevated. Frameworks, resource
/// packages, packages Windows marks non-removable and packages signed by the system are left out: none of them
/// is something a person installed or could remove.
/// </summary>
public sealed class AppxPackageReader : IAppxPackageSource
{
    internal const string Script =
        "Get-AppxPackage | Select Name,PackageFullName,PackageFamilyName,Publisher,Version,IsFramework," +
        "IsResourcePackage,NonRemovable,SignatureKind,InstallLocation | ConvertTo-Json";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>The PackageSignatureKind value of a package signed by the system.</summary>
    private const int SystemSignatureKind = 4;

    public async Task<IReadOnlyList<InstalledProgramEntry>?> ReadAsync(CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                "powershell", ["-NoProfile", "-NonInteractive", "-Command", Script], Timeout, cancellationToken);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        return result.TimedOut || result.ExitCode != 0 ? null : ParseJson(result.StandardOutput);
    }

    /// <summary>
    /// The entries of a `Get-AppxPackage | ConvertTo-Json` text. ConvertTo-Json writes a bare object when only
    /// one package exists and an array otherwise; empty or malformed text gives an empty list. Null for text
    /// that is not JSON at all, so a failed read is told apart from no packages.
    /// </summary>
    public static IReadOnlyList<InstalledProgramEntry>? ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.Trim().TrimStart('﻿'));
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var elements = root.ValueKind switch
            {
                JsonValueKind.Array => root.EnumerateArray().ToList(),
                JsonValueKind.Object => [root],
                _ => new List<JsonElement>(),
            };

            var result = new List<InstalledProgramEntry>();
            foreach (var element in elements)
            {
                if (element.ValueKind == JsonValueKind.Object && ToEntry(element) is { } entry)
                    result.Add(entry);
            }

            return result;
        }
    }

    private static InstalledProgramEntry? ToEntry(JsonElement package)
    {
        if (Flag(package, "IsFramework") || Flag(package, "IsResourcePackage") || Flag(package, "NonRemovable"))
            return null;

        if (IsSystemSigned(package))
            return null;

        var name = Text(package, "Name");
        var fullName = Text(package, "PackageFullName");
        if (name.Length == 0 || fullName.Length == 0)
            return null;

        var version = VersionText(package);
        if (version.Length == 0)
            version = VersionFromFullName(fullName);

        return new InstalledProgramEntry
        {
            Key = "appx:" + fullName,
            Kind = UninstallEntryKind.StoreApp,
            Name = name,
            Publisher = Text(package, "Publisher"),
            Version = version,
            InstallLocation = Text(package, "InstallLocation"),
            Scope = UninstallScope.User,
            PackageFullName = fullName,
            PackageFamilyName = Text(package, "PackageFamilyName") is { Length: > 0 } family ? family : null,
        };
    }

    private static bool Flag(JsonElement package, string property) =>
        package.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>PowerShell 5 writes the signature kind as a number, newer builds as its name.</summary>
    private static bool IsSystemSigned(JsonElement package)
    {
        if (!package.TryGetProperty("SignatureKind", out var kind))
            return false;

        return kind.ValueKind switch
        {
            JsonValueKind.Number => kind.TryGetInt32(out var number) && number == SystemSignatureKind,
            JsonValueKind.String => string.Equals(kind.GetString(), "System", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static string Text(JsonElement package, string property) =>
        package.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    /// <summary>The version as text, or from the Major/Minor/Build/Revision parts a PowerShell 5 Version object gets.</summary>
    private static string VersionText(JsonElement package)
    {
        if (!package.TryGetProperty("Version", out var version))
            return string.Empty;

        if (version.ValueKind == JsonValueKind.String)
            return version.GetString()?.Trim() ?? string.Empty;

        if (version.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var parts = new List<string>();
        foreach (var part in new[] { "Major", "Minor", "Build", "Revision" })
        {
            if (!version.TryGetProperty(part, out var value) || !value.TryGetInt32(out var number) || number < 0)
                break;
            parts.Add(number.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join('.', parts);
    }

    /// <summary>A package full name reads Name_Version_Architecture_ResourceId_PublisherId.</summary>
    internal static string VersionFromFullName(string fullName)
    {
        var parts = fullName.Split('_');
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }
}

/// <summary>`winget list` run for the whole machine; absent winget or a failed run is simply no text.</summary>
public sealed class WingetListReader : IWingetListSource
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public async Task<string?> ListAsync(CancellationToken cancellationToken)
    {
        if (!ProcessRunner.Exists("winget"))
            return null;

        try
        {
            var result = await ProcessRunner.RunAsync(
                "winget", ["list", "--accept-source-agreements", "--disable-interactivity"], Timeout, cancellationToken);
            return result.TimedOut || result.ExitCode != 0 ? null : result.StandardOutput;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
