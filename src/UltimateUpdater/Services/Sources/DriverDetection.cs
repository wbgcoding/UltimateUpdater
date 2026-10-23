using System.Management;
using System.Runtime.Versioning;

namespace UltimateUpdater.Services.Sources;

public enum GpuVendor
{
    Nvidia,
    Amd,
    Intel,
}

/// <summary>One display adapter as Windows reports it. DriverVersion is the raw WMI string.</summary>
public sealed record GpuInfo(GpuVendor Vendor, string Name, string? DriverVersion, DateTimeOffset? DriverDate = null);

/// <summary>
/// Finds the display adapters and who made them, from WMI. The PCI vendor id in the device's
/// PNPDeviceID is the reliable signal (VEN_10DE = NVIDIA, VEN_1002 = AMD, VEN_8086 = Intel); the
/// friendly name is only for display. Virtual and Basic-Display adapters (no "PCI\" prefix) are
/// skipped so a remote session or a fresh install does not produce a driver row for nothing.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DriverDetection
{
    // Up to 3 driver sources (NVIDIA/AMD/Intel) each call this from both IsAvailableAsync and ScanAsync
    // during the same scan pass, without a cache that is up to 6 redundant WMI round-trips for hardware
    // that never changes mid-scan. The TTL just needs to outlive one scan pass, not survive to the next.
    private static readonly object CacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static IReadOnlyList<GpuInfo>? _cached;
    private static DateTime _cachedAtUtc;

    /// <summary>A hung WMI provider must not cost more than this, without a cap, one hung provider can
    /// stall the whole scan for up to twelve minutes.</summary>
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(5);

    public static IReadOnlyList<GpuInfo> DetectGpus()
    {
        // Held for the WHOLE detection, not just the cache read/write: NVIDIA/AMD/Intel each
        // call this from both IsAvailableAsync and ScanAsync; up to six calls at cold start, all racing
        // an empty cache and each running the same expensive WMI query in parallel before any of them
        // could populate it. A caller that loses the race now finds the cache already warm instead.
        lock (CacheLock)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                return _cached;

            var gpus = DetectGpusCore();
            _cached = gpus;
            _cachedAtUtc = DateTime.UtcNow;
            return gpus;
        }
    }

    private static List<GpuInfo> DetectGpusCore()
    {
        var gpus = new List<GpuInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                string.Empty,
                "SELECT Name, PNPDeviceID, DriverVersion, DriverDate FROM Win32_VideoController",
                new EnumerationOptions { Timeout = WmiTimeout });

            using var results = searcher.Get();
            foreach (var device in results)
            {
                using (device)
                {
                    var pnpId = device["PNPDeviceID"] as string ?? string.Empty;
                    if (!pnpId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var vendor = VendorFromPnpId(pnpId);
                    if (vendor is null)
                        continue;

                    var name = (device["Name"] as string ?? string.Empty).Trim();
                    gpus.Add(new GpuInfo(vendor.Value, name, device["DriverVersion"] as string,
                        ParseDriverDate(device["DriverDate"] as string)));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException
                                      or System.Runtime.InteropServices.COMException
                                      or UnauthorizedAccessException)
        {
            // WMI unavailable, refusing, or timed out: report no adapters rather than failing (or
            // hanging) the whole scan.
        }

        return gpus;
    }

    /// <summary>WMI's CIM_DATETIME form ("20240115000000.000000-000"). Fails closed: a malformed or
    /// absent value is no date, never a guess.</summary>
    private static DateTimeOffset? ParseDriverDate(string? cimDateTime)
    {
        if (string.IsNullOrWhiteSpace(cimDateTime))
            return null;

        try
        {
            return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(cimDateTime));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static GpuVendor? VendorFromPnpId(string pnpId)
    {
        if (pnpId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Nvidia;
        if (pnpId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Amd;
        if (pnpId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Intel;

        return null;
    }

    /// <summary>
    /// NVIDIA's WMI DriverVersion is zero-padded and unrelated-looking, e.g. "32.0.15.6094". The
    /// marketing version driver lookups compare against is the last five digits of the dot-stripped
    /// string with a dot after the third: 56094 → "560.94". Pure, so it is unit-tested directly.
    /// </summary>
    public static string? DecodeNvidiaVersion(string? wmiDriverVersion)
    {
        if (string.IsNullOrWhiteSpace(wmiDriverVersion))
            return null;

        var digits = new string(wmiDriverVersion.Where(char.IsDigit).ToArray());
        if (digits.Length < 5)
            return null;

        var last5 = digits[^5..];
        return $"{last5[..3]}.{last5[3..]}";
    }
}
