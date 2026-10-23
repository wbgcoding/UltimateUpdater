using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="RegistryBackupService.ExportKeyTreeText"/> replaced shelling out to `reg export`
/// (a temp file the same unprivileged user could tamper with) with a direct, in-memory read of the same
/// values through the .NET registry API. This proves the in-memory text is byte-for-byte what `reg export`
/// itself would have produced for the same key, one value of every kind it can hold: a scratch key is
/// created under HKCU, exported for real with reg.exe to get the reference text, then deleted, and the two
/// texts are compared.
/// </summary>
public class RegistryBackupExportFormatTests
{
    [Fact]
    public async Task ExportKeyTreeText_matches_a_real_reg_export_for_every_value_kind()
    {
        var keyName = "WuRegBackupRoundTrip" + Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\{keyName}";
        var fullKeyPath = $@"HKCU\{relativePath}";
        var tempReg = Path.Combine(Path.GetTempPath(), $"wu-roundtrip-{Guid.NewGuid():N}.reg");

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath))
            {
                Assert.NotNull(key);
                key!.SetValue(string.Empty, "default value", RegistryValueKind.String);
                key.SetValue("StrVal", "C:\\Program Files\\Test \"Quoted\" App\\x.exe", RegistryValueKind.String);
                key.SetValue("ExpVal", "%PATH%", RegistryValueKind.ExpandString);
                key.SetValue("MultiVal", new[] { "a", "b" }, RegistryValueKind.MultiString);
                key.SetValue("BinVal", new byte[] { 1, 2, 3, 0xAB }, RegistryValueKind.Binary);
                key.SetValue("DwordVal", 1, RegistryValueKind.DWord);
                key.SetValue("QwordVal", 123456789012L, RegistryValueKind.QWord);

                using var sub = key.CreateSubKey("Sub");
                Assert.NotNull(sub);
                sub!.SetValue("X", "y", RegistryValueKind.String);
            }

            var expected = await ExportWithRegExeAsync(fullKeyPath, tempReg);
            var actual = RegistryBackupService.ExportKeyTreeText(fullKeyPath);

            Assert.Equal(expected, actual);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            try { File.Delete(tempReg); } catch { }
        }
    }

    /// <summary>
    /// The happy-path test above never exercised a value long enough to hit `reg
    /// export`'s own 80-column line wrapping, an empty MULTI_SZ, a MULTI_SZ with an empty member, a
    /// malformed REG_DWORD (6 and 12 raw bytes instead of exactly 4, only reachable through the raw Win32
    /// API since <see cref="RegistryKey.SetValue(string?,object)"/> refuses to write one), a
    /// REG_DWORD_BIG_ENDIAN (type 5, likewise raw-API only, .NET's <see cref="RegistryValueKind"/> has no
    /// member for it), or a REG_SZ with an embedded NUL. Real `reg export` line-wraps long hex data with a
    /// trailing backslash plus a continuation line; <see cref="RegistryBackupService.ExportKeyTreeText"/>
    /// deliberately never wraps (see <see cref="DewrapRegExport"/>), so the wrapped reference text is
    /// unwrapped before the comparison, proving the two are the same DATA rather than the same layout.
    /// </summary>
    [Fact]
    public async Task ExportKeyTreeText_matches_a_real_reg_export_for_edge_case_values()
    {
        var keyName = "WuRegBackupEdgeCases" + Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\{keyName}";
        var fullKeyPath = $@"HKCU\{relativePath}";
        var tempReg = Path.Combine(Path.GetTempPath(), $"wu-edgecases-{Guid.NewGuid():N}.reg");

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath))
            {
                Assert.NotNull(key);

                // Long enough to force reg export's own line wrapping.
                key!.SetValue("LongExpand", new string('P', 250) + "%PATH%", RegistryValueKind.ExpandString);
                var longBinary = new byte[120];
                for (var i = 0; i < longBinary.Length; i++)
                    longBinary[i] = (byte)i;
                key.SetValue("LongBinary", longBinary, RegistryValueKind.Binary);

                key.SetValue("EmptyMulti", Array.Empty<string>(), RegistryValueKind.MultiString);
                key.SetValue("MultiWithEmptyMember", new[] { "a", "", "b" }, RegistryValueKind.MultiString);

                // Malformed/rare shapes RegistryKey.SetValue cannot express at all; written through the
                // same raw Win32 call the production code now reads with.
                SetRawValue(key, "SixByteDword", RegDwordType, [1, 0, 0, 0, 0xAA, 0xBB]);
                SetRawValue(key, "TwelveByteDword", RegDwordType, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
                SetRawValue(key, "BigEndianDword", RegDwordBigEndianType, [0, 0, 1, 0]);
                SetRawValue(key, "EmbeddedNul", RegSzType, Encoding.Unicode.GetBytes("a\0b\0"));
            }

            var expected = DewrapRegExport(await ExportWithRegExeAsync(fullKeyPath, tempReg));
            var actual = RegistryBackupService.ExportKeyTreeText(fullKeyPath);

            // "EmbeddedNul" is the one deliberate divergence, held out of the blanket comparison below and
            // asserted on its own: real `reg export` itself writes an embedded NUL as a quoted string CUT
            // OFF at the NUL (proven by the assert against its own reference line here), the exact data
            // loss this fix named as a bug; ExportKeyTreeText writes it as hex(1) (REG_SZ's own type tag)
            // with the full raw bytes instead, on purpose, because there is no lossless quoted form for
            // this content. Every OTHER edge case is still required to match `reg export` byte-for-byte.
            Assert.Contains("\"EmbeddedNul\"=\"a\"\r\n", expected);
            Assert.Contains("\"EmbeddedNul\"=hex(1):61,00,00,00,62,00,00,00\r\n", actual);

            Assert.Equal(
                WithoutLine(expected, "\"EmbeddedNul\"=").TrimEnd(),
                WithoutLine(actual, "\"EmbeddedNul\"=").TrimEnd());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
            try { File.Delete(tempReg); } catch { }
        }
    }

    private static async Task<string> ExportWithRegExeAsync(string fullKeyPath, string tempReg)
    {
        var regExePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");
        using (var export = Process.Start(new ProcessStartInfo
        {
            FileName = regExePath,
            Arguments = $"export \"{fullKeyPath}\" \"{tempReg}\" /y",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }))
        {
            Assert.NotNull(export);
            export!.WaitForExit(10000);
            Assert.Equal(0, export.ExitCode);
        }

        return await File.ReadAllTextAsync(tempReg, Encoding.Unicode);
    }

    /// <summary>
    /// Undoes `reg export`'s own 80-column line wrapping (a trailing space, backslash, CRLF, then the
    /// continuation indented by a single space) so a wrapped reference export can be compared against
    /// <see cref="RegistryBackupService.ExportKeyTreeText"/>'s own never-wrapped output by content.
    /// </summary>
    private static string DewrapRegExport(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            while (line.EndsWith('\\') && i + 1 < lines.Length)
            {
                i++;
                line = line[..^1] + lines[i].TrimStart(' ');
            }

            sb.Append(line).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>Drops every line starting with <paramref name="linePrefix"/>, used to hold one known,
    /// deliberate divergence out of an otherwise-exact comparison.</summary>
    private static string WithoutLine(string text, string linePrefix)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Where(line => !line.StartsWith(linePrefix, StringComparison.Ordinal)));
    }

    private const uint RegSzType = 1;
    private const uint RegDwordType = 4;
    private const uint RegDwordBigEndianType = 5;

    private static void SetRawValue(RegistryKey key, string valueName, uint regType, byte[] data)
    {
        var result = RegSetValueExW(key.Handle, valueName, 0, regType, data, data.Length);
        Assert.Equal(0, result);
    }

    [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(
        SafeRegistryHandle hKey, string lpValueName, int reserved, uint dwType, byte[] lpData, int cbData);

    private const uint RegLinkType = 6;

    /// <summary>
    /// An ACCESS-DENIED open used to be treated exactly like "the key vanished", so a
    /// same-user HKCU subkey with a deny-read ACE was silently dropped from the backup, the rest of the
    /// tree still came back as a "successful" export, and the cleanup pass would then delete real,
    /// un-backed-up data under it. <see cref="RegistryBackupService.ExportKeyTreeText"/> must now fail
    /// closed instead: an open failure other than ERROR_FILE_NOT_FOUND throws.
    /// </summary>
    [Fact]
    public void ExportKeyTreeText_throws_when_a_subkey_denies_read_access()
    {
        var keyName = "WuRegBackupDenySub" + Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\{keyName}";
        var fullKeyPath = $@"HKCU\{relativePath}";
        var subRelativePath = $@"{relativePath}\Sub";

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath))
            {
                Assert.NotNull(key);
                key!.SetValue("Marker", "still here", RegistryValueKind.String);
                using var sub = key.CreateSubKey("Sub");
                Assert.NotNull(sub);
                sub!.SetValue("Hidden", "should never be silently skipped", RegistryValueKind.String);
            }

            DenyOwnReadAccess(subRelativePath);

            Assert.Throws<IOException>(() => RegistryBackupService.ExportKeyTreeText(fullKeyPath));
        }
        finally
        {
            RestoreOwnAccess(subRelativePath);
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
        }
    }

    /// <summary>Same as above, but the DENIED key is the backup root itself, the case where the old code's
    /// silent skip produced a header-only "successful" backup with nothing in it at all.</summary>
    [Fact]
    public void ExportKeyTreeText_throws_when_the_root_key_denies_read_access()
    {
        var keyName = "WuRegBackupDenyRoot" + Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\{keyName}";
        var fullKeyPath = $@"HKCU\{relativePath}";

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath))
            {
                Assert.NotNull(key);
                key!.SetValue("Marker", "still here", RegistryValueKind.String);
            }

            DenyOwnReadAccess(relativePath);

            Assert.Throws<IOException>(() => RegistryBackupService.ExportKeyTreeText(fullKeyPath));
        }
        finally
        {
            RestoreOwnAccess(relativePath);
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// A value literally named <c>SymbolicLinkValue</c> of type REG_LINK does not, on
    /// its own, make a key a real registry link (real links are only ever created through
    /// <c>RegCreateKeyEx</c> with <c>REG_OPTION_CREATE_LINK</c>) - this plants exactly that decoy on an
    /// otherwise completely ordinary root key the way an attacker with same-user write access could, via
    /// the raw <c>RegSetValueExW</c> call, the same one edge-case values above already use. The marker
    /// alone is never trusted, root or not: it is verified (opening it plainly lands right back on itself,
    /// not somewhere else, proving it fake), and only then backed up as the ordinary key it actually is,
    /// exactly like a decoy found below the root (see
    /// <see cref="ExportKeyTreeText_backs_up_a_child_with_a_planted_link_marker_as_an_ordinary_key"/>). A
    /// blanket refusal on the marker's word alone would have silently made a real backup root permanently
    /// un-backup-able the moment it happened to carry this value, whether or not it were ever a real link.
    /// </summary>
    [Fact]
    public void ExportKeyTreeText_backs_up_a_root_with_a_planted_link_marker_as_an_ordinary_key()
    {
        var keyName = "WuRegBackupDecoyRoot" + Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\{keyName}";
        var fullKeyPath = $@"HKCU\{relativePath}";

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath))
            {
                Assert.NotNull(key);
                key!.SetValue("Marker", "real data that must not be silently dropped", RegistryValueKind.String);
                SetRawValue(key, "SymbolicLinkValue", RegLinkType, Encoding.Unicode.GetBytes("\\Registry\\Machine\0"));
            }

            var actual = RegistryBackupService.ExportKeyTreeText(fullKeyPath);

            Assert.Contains(@"[HKEY_CURRENT_USER\" + relativePath + @"]", actual);
            Assert.Contains("\"Marker\"=\"real data that must not be silently dropped\"", actual);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// Follow-up to this fix: a VERIFIED real registry link sitting AT the backup root must not be refused
    /// wholesale either, or the whole root becomes permanently un-backup-able (and therefore permanently
    /// un-deletable) the moment it legitimately is one - proven here against a key that genuinely IS a
    /// real registry link on every 64-bit Windows install this project targets:
    /// <c>RegistryCleanupScanner</c>'s own App Paths root already scans and deletes through it via an
    /// ordinary (link-following) open, so the backup must cover the exact same, real, resolved data rather
    /// than refuse to look past the link. A regression here would silently break registry cleanup for
    /// every App Paths entry on any 64-bit machine, exactly the failure four <c>CleanupServiceTests</c>
    /// registry cases caught during self-test.
    /// </summary>
    [Fact]
    public void ExportKeyTreeText_follows_a_genuine_link_sitting_at_the_backup_root()
    {
        var text = RegistryBackupService.ExportKeyTreeText(
            @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths");

        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths]", text);
        Assert.True(text.Length > 100, "a followed link's real target must contribute real content, not a header-only section");
    }

    /// <summary>Same decoy marker, planted on a CHILD instead of the root: since it is proven fake (opening
    /// it plainly lands back on itself, not somewhere else), it must be backed up as the ordinary key it
    /// actually is rather than silently skipped.</summary>
    [Fact]
    public async Task ExportKeyTreeText_backs_up_a_child_with_a_planted_link_marker_as_an_ordinary_key()
    {
        var keyName = "WuRegBackupDecoyChild" + Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\{keyName}";
        var fullKeyPath = $@"HKCU\{relativePath}";

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath))
            {
                Assert.NotNull(key);
                key!.SetValue("Marker", "root value", RegistryValueKind.String);
                using var sub = key.CreateSubKey("Sub");
                Assert.NotNull(sub);
                sub!.SetValue("RealChildValue", "must survive the backup", RegistryValueKind.String);
                SetRawValue(sub, "SymbolicLinkValue", RegLinkType, Encoding.Unicode.GetBytes("\\Registry\\Machine\0"));
            }

            var actual = RegistryBackupService.ExportKeyTreeText(fullKeyPath);

            Assert.Contains(@"[HKEY_CURRENT_USER\" + relativePath + @"\Sub]", actual);
            Assert.Contains("\"RealChildValue\"=\"must survive the backup\"", actual);
            await Task.CompletedTask;
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// A value that keeps GROWING between the size query and the data read comes back
    /// as ERROR_MORE_DATA on the second Win32 call, and production's bounded retry must eventually exhaust
    /// its retry count and throw naming the value. This used to be proven with a real background thread
    /// racing a real HKCU key for up to 200 outside attempts, hoping to land inside the query/read window;
    /// on a fast or quiet machine it never did, and the test failed with nothing wrong in the product
    /// (flaky, forbidden). Driving <see cref="RegistryBackupService.TryReadRawValueWithRetryCore"/> with a
    /// fake attempt delegate that reports "more data" every time removes the race entirely: the loop under
    /// test is the exact one production runs, just fed a deterministic reader instead of two live Win32
    /// calls.
    /// </summary>
    [Fact]
    public void TryReadRawValueWithRetryCore_throws_when_every_attempt_reports_more_data()
    {
        const string valueName = "Growing";
        var attempts = 0;

        var ex = Assert.Throws<IOException>(() =>
            RegistryBackupService.TryReadRawValueWithRetryCore(
                maxRetries: 5,
                valueName,
                _ =>
                {
                    attempts++;
                    return new RegistryBackupService.RawReadAttempt(RegistryBackupService.RawReadOutcome.MoreData, 0, []);
                },
                out _,
                out _));

        Assert.Contains("kept growing", ex.Message);
        Assert.Contains(valueName, ex.Message);
        Assert.Equal(5, attempts);
    }

    /// <summary>
    /// Companion to this fix: a value that grows once and then holds still must still be read
    /// successfully, proving the retry is not merely bounded but actually recovers once the race stops.
    /// </summary>
    [Fact]
    public void TryReadRawValueWithRetryCore_succeeds_after_growing_once()
    {
        const uint regBinaryType = 3;
        var expectedBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var attempts = 0;

        var found = RegistryBackupService.TryReadRawValueWithRetryCore(
            maxRetries: 5,
            "Growing",
            _ =>
            {
                attempts++;
                return attempts == 1
                    ? new RegistryBackupService.RawReadAttempt(RegistryBackupService.RawReadOutcome.MoreData, 0, [])
                    : new RegistryBackupService.RawReadAttempt(RegistryBackupService.RawReadOutcome.Success, regBinaryType, expectedBytes);
            },
            out var regType,
            out var raw);

        Assert.True(found);
        Assert.Equal(2, attempts);
        Assert.Equal(regBinaryType, regType);
        Assert.Equal(expectedBytes, raw);
    }

    /// <summary>
    /// A REG_SZ that is not genuinely clean UTF-16 text (here: a lone, unpaired
    /// surrogate half, and separately a raw CR/LF) used to be quoted anyway, silently altering the value on
    /// restore (<see cref="Encoding.Unicode"/> replaces the invalid surrogate on decode, so re-encoding the
    /// quoted text back would no longer match the original bytes at all; a raw CR/LF inside a quoted
    /// REGEDIT5 string corrupts the single-line format on import even though it decodes back cleanly). Both
    /// must fall back to <c>hex(1):</c> with the exact original bytes instead.
    /// </summary>
    [Fact]
    public void ExportKeyTreeText_writes_a_lone_surrogate_and_an_embedded_crlf_as_hex1()
    {
        var keyName = "WuRegBackupBadSz" + Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\{keyName}";
        var fullKeyPath = $@"HKCU\{relativePath}";

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath))
            {
                Assert.NotNull(key);

                // A lone high-surrogate half (U+D800) with no low surrogate to pair it with, plus a
                // trailing NUL terminator the same way a real reg export would carry one.
                var loneSurrogateBytes = new byte[] { 0x41, 0x00, 0x00, 0xD8, 0x42, 0x00, 0x00, 0x00 };
                SetRawValue(key, "LoneSurrogate", RegSzType, loneSurrogateBytes);

                // A raw CR/LF, otherwise perfectly valid UTF-16, plus a trailing NUL terminator.
                var crlfBytes = Encoding.Unicode.GetBytes("a\r\nb\0");
                SetRawValue(key, "EmbeddedCrLf", RegSzType, crlfBytes);
            }

            var actual = RegistryBackupService.ExportKeyTreeText(fullKeyPath);

            var loneSurrogateHex = string.Join(",", new byte[] { 0x41, 0x00, 0x00, 0xD8, 0x42, 0x00, 0x00, 0x00 }.Select(b => b.ToString("x2")));
            var crlfHex = string.Join(",", Encoding.Unicode.GetBytes("a\r\nb\0").Select(b => b.ToString("x2")));

            Assert.Contains($"\"LoneSurrogate\"=hex(1):{loneSurrogateHex}\r\n", actual);
            Assert.Contains($"\"EmbeddedCrLf\"=hex(1):{crlfHex}\r\n", actual);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// <c>RegQueryValueExW</c>'s value-name parameter is marshaled as a null-terminated
    /// string, so a value NAME with an embedded NUL (written here through a raw, manually null-terminated
    /// buffer, the one way to actually create one, since the ordinary managed APIs marshal names the exact
    /// same lossy way on the write side too) would make a read silently target a DIFFERENT, shorter value
    /// than the one <c>GetValueNames</c> just enumerated. This must fail the whole backup closed rather
    /// than silently read (or silently skip) the wrong thing.
    /// </summary>
    [Fact]
    public void ExportKeyTreeText_throws_on_a_value_name_with_an_embedded_nul()
    {
        var keyName = "WuRegBackupNulName" + Guid.NewGuid().ToString("N");
        var relativePath = $@"Software\{keyName}";
        var fullKeyPath = $@"HKCU\{relativePath}";
        string[] namesSeen;

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(relativePath))
            {
                Assert.NotNull(key);
                SetRawValueName(key, "a\0b", RegSzType, Encoding.Unicode.GetBytes("x"));
                namesSeen = key!.GetValueNames();
            }

            // Sanity: .NET's own enumeration sees the full name including the embedded NUL, proving the
            // value really was written with that shape and this is not merely testing a name .NET itself
            // would already have truncated on write.
            Assert.Contains("a\0b", namesSeen);

            Assert.Throws<IOException>(() => RegistryBackupService.ExportKeyTreeText(fullKeyPath));
        }
        finally
        {
            // RegistryKey.DeleteSubKeyTree deletes each VALUE by name first, and that name goes through the
            // exact same null-terminated marshaling this whole test exists to prove is unsafe: it would try
            // to delete a value named "a" (truncated at the embedded NUL), which does not exist, and fail
            // with a misleading "the specified registry key does not exist". NtDeleteKey takes only the
            // already-open HANDLE, no name marshaling at all, so it deletes the key (and every value under
            // it, regardless of name) in one step.
            // NtDeleteKey needs the handle's own DELETE right explicitly, `writable: true` alone does not
            // request it (KEY_WRITE has no DELETE bit).
            using var key = Registry.CurrentUser.OpenSubKey(relativePath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl);
            if (key is not null)
                NtDeleteKey(key.Handle);
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtDeleteKey(SafeRegistryHandle keyHandle);

    private static SecurityIdentifier CurrentUserSid => WindowsIdentity.GetCurrent().User!;

    /// <summary>
    /// Denies only <see cref="RegistryRights.QueryValues"/> (KEY_QUERY_VALUE), the one right
    /// <c>RegQueryValueExW</c> actually needs: a Windows access check fails the WHOLE requested mask if any
    /// single requested bit is denied, so this alone is enough to make production's `KeyRead`-masked open
    /// fail closed, the same as a real deny-read ACE would. Deliberately narrower than denying the whole
    /// composite "read" right, which also carries READ_CONTROL and would then block even the ACL cleanup
    /// this test's own `finally` needs to do afterward.
    /// </summary>
    private static void DenyOwnReadAccess(string relativePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(relativePath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl);
        Assert.NotNull(key);
        var security = key!.GetAccessControl();
        security.AddAccessRule(new RegistryAccessRule(CurrentUserSid, RegistryRights.QueryValues, AccessControlType.Deny));
        key.SetAccessControl(security);
    }

    /// <summary>Reopens with only READ_CONTROL/WRITE_DAC, rights <see cref="DenyOwnReadAccess"/> never
    /// touched, so this works even while the deny rule it is about to remove is still active.</summary>
    private static void RestoreOwnAccess(string relativePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            relativePath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions);
        if (key is null)
            return;

        var security = key.GetAccessControl();
        foreach (RegistryAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference.Equals(CurrentUserSid) && rule.AccessControlType == AccessControlType.Deny)
                security.RemoveAccessRule(rule);
        }

        key.SetAccessControl(security);
    }

    /// <summary>
    /// Writes a value whose NAME has an embedded NUL. <c>RegSetValueExW</c>'s own name parameter is a
    /// plain null-terminated <c>LPCWSTR</c> (Windows itself, not just .NET's marshaller, stops at the first
    /// NUL), so the ordinary API cannot create this shape at all; <c>NtSetValueKey</c>'s
    /// <c>UNICODE_STRING</c> carries an explicit byte LENGTH instead of relying on a terminator, which is
    /// the one way to actually get this data into the registry to prove production code fails closed on it.
    /// </summary>
    private static void SetRawValueName(RegistryKey key, string name, uint regType, byte[] data)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var bufferPtr = Marshal.AllocHGlobal(Math.Max(nameBytes.Length, 1));
        try
        {
            Marshal.Copy(nameBytes, 0, bufferPtr, nameBytes.Length);
            var unicodeName = new UnicodeString
            {
                Length = (ushort)nameBytes.Length,
                MaximumLength = (ushort)nameBytes.Length,
                Buffer = bufferPtr,
            };

            var status = NtSetValueKey(key.Handle, ref unicodeName, 0, regType, data, data.Length);
            Assert.Equal(0, status);
        }
        finally
        {
            Marshal.FreeHGlobal(bufferPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetValueKey(
        SafeRegistryHandle keyHandle, ref UnicodeString valueName, int titleIndex, uint type, byte[] data, int dataSize);

}
