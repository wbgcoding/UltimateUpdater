using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
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
/// packages, bundles, packages Windows marks non-removable, packages signed by the system and packages whose manifest
/// has no app a person could open (background helpers, language packs) are left out: none of them is something a
/// person installed or could remove. A row carries the manifest's display name and publisher display name, not the
/// package name and the certificate's distinguished name.
/// </summary>
public sealed class AppxPackageReader : IAppxPackageSource
{
    /// <summary>
    /// One object per package. The manifest is read per package inside its own try, so a package whose manifest cannot be
    /// read still lists (the three manifest fields stay empty) instead of dropping the whole result.
    /// </summary>
    internal const string Script =
        "Get-AppxPackage | ForEach-Object { $p = $_; $d = $null; $b = $null; $v = $null; " +
        "try { $m = Get-AppxPackageManifest -Package $p; $d = [string]$m.Package.Properties.DisplayName; " +
        "$b = [string]$m.Package.Properties.PublisherDisplayName; " +
        "$v = @($m.Package.Applications.Application | Where-Object { $_ -and $_.AppListEntry -ne 'none' }).Count -gt 0 } catch { }; " +
        "[pscustomobject]@{ Name = $p.Name; PackageFullName = $p.PackageFullName; PackageFamilyName = $p.PackageFamilyName; " +
        "Publisher = $p.Publisher; Version = $p.Version; IsFramework = $p.IsFramework; IsResourcePackage = $p.IsResourcePackage; " +
        "IsBundle = $p.IsBundle; NonRemovable = $p.NonRemovable; SignatureKind = $p.SignatureKind; " +
        "InstallLocation = $p.InstallLocation; DisplayName = $d; PublisherDisplayName = $b; HasVisibleApp = $v } } | ConvertTo-Json";

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
    public static IReadOnlyList<InstalledProgramEntry>? ParseJson(string json, Func<string, string?>? resolveIndirect = null)
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
                if (element.ValueKind == JsonValueKind.Object && ToEntry(element, resolveIndirect ?? IndirectString.Load) is { } entry)
                    result.Add(entry);
            }

            return result;
        }
    }

    private static InstalledProgramEntry? ToEntry(JsonElement package, Func<string, string?> resolveIndirect)
    {
        if (Flag(package, "IsFramework") || Flag(package, "IsResourcePackage") || Flag(package, "IsBundle") || Flag(package, "NonRemovable"))
            return null;

        if (IsSystemSigned(package))
            return null;

        // Only a manifest that was read and offers no app to open counts: an unreadable one leaves the row in.
        if (package.TryGetProperty("HasVisibleApp", out var visible) && visible.ValueKind == JsonValueKind.False)
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
            Name = DisplayName(package, name, fullName, resolveIndirect),
            Publisher = PublisherName(package, fullName, resolveIndirect),
            Version = version,
            InstallLocation = Text(package, "InstallLocation"),
            Scope = UninstallScope.User,
            PackageFullName = fullName,
            PackageFamilyName = Text(package, "PackageFamilyName") is { Length: > 0 } family ? family : null,
        };
    }

    /// <summary>The manifest's display name; the package name when there is none or it cannot be resolved.</summary>
    private static string DisplayName(JsonElement package, string packageName, string fullName, Func<string, string?> resolveIndirect) =>
        ResolveText(Text(package, "DisplayName"), packageName, fullName, resolveIndirect) ?? packageName;

    /// <summary>The manifest's publisher display name; else the common name of the certificate's subject; else the subject itself.</summary>
    private static string PublisherName(JsonElement package, string fullName, Func<string, string?> resolveIndirect)
    {
        var packageName = fullName.Split('_')[0];
        if (ResolveText(Text(package, "PublisherDisplayName"), packageName, fullName, resolveIndirect) is { } display)
            return display;

        var subject = Text(package, "Publisher");
        return CommonName(subject) ?? subject;
    }

    /// <summary>
    /// A manifest text as it should show: plain text as it is, an `ms-resource:` reference through the package's
    /// resources. Null when there is nothing or the reference does not resolve. A failure of one lookup is no failure
    /// of the list.
    /// </summary>
    internal static string? ResolveText(string raw, string packageName, string fullName, Func<string, string?> resolveIndirect)
    {
        if (raw.Length == 0)
            return null;

        if (!raw.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
            return raw;

        // "ms-resource:Name" is relative to the package's default resource map, which the indirect form wants spelled out.
        var candidates = new List<string> { $"@{{{fullName}?{raw}}}" };
        if (!raw.StartsWith(ResourcePrefix + "/", StringComparison.OrdinalIgnoreCase))
            candidates.Add($"@{{{fullName}?ms-resource://{packageName}/resources/{raw[ResourcePrefix.Length..]}}}");

        foreach (var candidate in candidates)
        {
            try
            {
                if (resolveIndirect(candidate) is { Length: > 0 } text
                    && !text.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return text.Trim();
                }
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
            {
                // this reference stays unresolved; the next candidate or the package name stands in
            }
        }

        return null;
    }

    private const string ResourcePrefix = "ms-resource:";

    /// <summary>The CN value of a distinguished name such as "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond", or null.</summary>
    internal static string? CommonName(string distinguishedName)
    {
        foreach (var part in SplitDistinguishedName(distinguishedName))
        {
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return part[3..].Trim().Trim('"') is { Length: > 0 } name ? name : null;
        }

        return null;
    }

    /// <summary>The parts of a distinguished name, split at commas that are not inside quotes.</summary>
    private static IEnumerable<string> SplitDistinguishedName(string value)
    {
        var part = new StringBuilder();
        var quoted = false;
        foreach (var c in value)
        {
            if (c == '"')
                quoted = !quoted;

            if (c == ',' && !quoted)
            {
                yield return part.ToString().Trim();
                part.Clear();
            }
            else
            {
                part.Append(c);
            }
        }

        yield return part.ToString().Trim();
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

/// <summary>Resolves an `@{PackageFullName?ms-resource://...}` reference to the text in the package's resources.</summary>
internal static class IndirectString
{
    private const int BufferLength = 512;

    /// <summary>The text, or null when the reference does not resolve (the call fails, or the text comes back empty).</summary>
    internal static string? Load(string source)
    {
        var buffer = new StringBuilder(BufferLength);
        return SHLoadIndirectString(source, buffer, BufferLength, IntPtr.Zero) >= 0 && buffer.Length > 0 ? buffer.ToString() : null;
    }

    // DllImport rather than LibraryImport: the source generator would require unsafe code to be enabled.
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int outputLength, IntPtr reserved);
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
