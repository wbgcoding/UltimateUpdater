using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

public class ExitRequestListenerTests
{
    private const uint ReadControl = 0x00020000;
    private const uint Synchronize = 0x00100000;
    private const uint DaclInformation = 0x4;
    private const uint LabelInformation = 0x10;

    private static string UniqueFolder() => @"C:\Test\" + Guid.NewGuid().ToString("N");

    [Fact]
    public void The_name_is_the_prefix_the_sid_and_16_hex_digits_of_the_lower_case_folder_hash()
    {
        var folder = @"C:\Users\Someone\AppData\Local\Programs\Ultimate Updater";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToLowerInvariant())))[..16].ToLowerInvariant();

        var name = ExitRequestListener.BuildName("S-1-5-21-1-2-3-1001", folder);

        Assert.Equal(@"Local\UltimateUpdater-Exit-S-1-5-21-1-2-3-1001-" + expected, name);
        Assert.Matches(@"-[0-9a-f]{16}$", name);
    }

    [Fact]
    public void The_folder_hash_ignores_case_and_a_trailing_separator()
    {
        var plain = ExitRequestListener.FolderHash(@"C:\Apps\Updater");

        Assert.Equal(plain, ExitRequestListener.FolderHash(@"c:\APPS\updater"));
        Assert.Equal(plain, ExitRequestListener.FolderHash(@"C:\Apps\Updater\"));
    }

    [Fact]
    public void Another_folder_or_another_user_gives_another_name()
    {
        var baseName = ExitRequestListener.BuildName("S-1-5-21-1", @"C:\Apps\A");

        Assert.NotEqual(baseName, ExitRequestListener.BuildName("S-1-5-21-1", @"C:\Apps\B"));
        Assert.NotEqual(baseName, ExitRequestListener.BuildName("S-1-5-21-2", @"C:\Apps\A"));
    }

    [Fact]
    public void A_signal_reaches_the_callback_every_time_it_is_set()
    {
        var count = 0;
        using var seen = new SemaphoreSlim(0);
        using var live = ExitRequestListener.Start(
            () => { Interlocked.Increment(ref count); seen.Release(); },
            userSid: NamedObjectSecurity.CurrentUserSid(), exeFolder: UniqueFolder());
        Assert.True(live.IsListening);

        using var signal = EventWaitHandle.OpenExisting(live.Name);
        signal.Set();
        Assert.True(seen.Wait(TimeSpan.FromSeconds(10)));
        signal.Set();
        Assert.True(seen.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, count);
    }

    [Fact]
    public void A_callback_that_throws_does_not_end_the_listener()
    {
        var calls = 0;
        using var seen = new SemaphoreSlim(0);
        using var listener = ExitRequestListener.Start(
            () =>
            {
                Interlocked.Increment(ref calls);
                seen.Release();
                throw new InvalidOperationException("boom");
            },
            userSid: NamedObjectSecurity.CurrentUserSid(), exeFolder: UniqueFolder());

        using var signal = EventWaitHandle.OpenExisting(listener.Name);
        signal.Set();
        Assert.True(seen.Wait(TimeSpan.FromSeconds(10)));
        signal.Set();
        Assert.True(seen.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void The_event_is_gone_once_the_listener_is_disposed()
    {
        var listener = ExitRequestListener.Start(() => { }, exeFolder: UniqueFolder());
        Assert.True(listener.IsListening);
        Assert.True(Exists(listener.Name));

        listener.Dispose();

        Assert.False(Exists(listener.Name));
        Assert.False(listener.IsListening);
    }

    /// <summary>
    /// The person may signal and wait (modify state plus synchronize, nothing more), SYSTEM and Administrators
    /// have everything, nobody else anything, and nothing is inherited.
    /// </summary>
    [Fact]
    public void The_events_dacl_lets_the_user_signal_and_nobody_else_in()
    {
        var sid = NamedObjectSecurity.CurrentUserSid();
        using var listener = ExitRequestListener.Start(() => { }, userSid: sid, exeFolder: UniqueFolder());

        var dacl = ReadSddl(OpenEventW, listener.Name, DaclInformation);

        Assert.StartsWith("D:P", dacl);
        Assert.Contains($"(A;;0x100002;;;{sid})", dacl);
        Assert.Contains("(A;;0x1f0003;;;SY)", dacl);
        Assert.Contains("(A;;0x1f0003;;;BA)", dacl);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(dacl, @"\(A;").Count);
    }

    [Fact]
    public void The_event_carries_a_medium_label_that_forbids_writing_up()
    {
        using var listener = ExitRequestListener.Start(() => { }, exeFolder: UniqueFolder());

        var label = ReadSddl(OpenEventW, listener.Name, LabelInformation);

        // Windows reports the auto-inherited flag (S:AI) on read-back; the label ACE itself is what is pinned
        Assert.StartsWith("S:", label);
        Assert.EndsWith("(ML;;NW;;;ME)", label);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(label, @"\(ML;").Count);
    }

    [Fact]
    public void The_busy_mutex_can_be_opened_with_synchronize_alone_and_by_nobody_else()
    {
        var sid = "test-" + Guid.NewGuid().ToString("N");
        using var signal = new BusySignal(sid);
        signal.Set(true);
        var name = BusySignal.NamePrefix + sid;

        var dacl = ReadSddl(OpenMutexW, name, DaclInformation);

        Assert.Contains($"(A;;0x100000;;;{NamedObjectSecurity.CurrentUserSid()})", dacl);
        Assert.Contains("(A;;0x1f0001;;;SY)", dacl);
        Assert.Contains("(A;;0x1f0001;;;BA)", dacl);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(dacl, @"\(A;").Count);
        var probe = OpenMutexW(Synchronize, false, name);
        Assert.NotEqual(IntPtr.Zero, probe);
        CloseHandle(probe);
    }

    /// <summary>Somebody created the name first, with a default DACL they or others can signal: the app must not
    /// listen on an object it did not make, and the squatter's signal must do nothing.</summary>
    [Fact]
    public void A_squatted_event_name_is_never_listened_on()
    {
        var sid = NamedObjectSecurity.CurrentUserSid();
        var folder = UniqueFolder();
        using var squatter = new EventWaitHandle(false, EventResetMode.AutoReset, ExitRequestListener.BuildName(sid, folder));
        var calls = 0;

        using var listener = ExitRequestListener.Start(() => Interlocked.Increment(ref calls), userSid: sid, exeFolder: folder);

        Assert.False(listener.IsListening);
        squatter.Set();
        Thread.Sleep(300);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_squatter_of_another_object_kind_does_not_crash_the_start()
    {
        var sid = NamedObjectSecurity.CurrentUserSid();
        var folder = UniqueFolder();
        using var squatter = new Mutex(false, ExitRequestListener.BuildName(sid, folder));

        using var listener = ExitRequestListener.Start(() => { }, userSid: sid, exeFolder: folder);

        Assert.False(listener.IsListening);
    }

    [Fact]
    public void A_name_that_is_free_again_can_be_used_by_the_next_start()
    {
        var sid = NamedObjectSecurity.CurrentUserSid();
        var folder = UniqueFolder();
        var squatter = new EventWaitHandle(false, EventResetMode.AutoReset, ExitRequestListener.BuildName(sid, folder));
        using (var blocked = ExitRequestListener.Start(() => { }, userSid: sid, exeFolder: folder))
            Assert.False(blocked.IsListening);

        squatter.Dispose();

        using var listener = ExitRequestListener.Start(() => { }, userSid: sid, exeFolder: folder);
        Assert.True(listener.IsListening);
    }

    [Fact]
    public void An_unknown_folder_means_no_listener_instead_of_a_crash()
    {
        using var listener = ExitRequestListener.Start(() => { }, exeFolder: "");

        Assert.False(listener.IsListening);
    }

    private static bool Exists(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var found))
            return false;

        found.Dispose();
        return true;
    }

    private static string ReadSddl(Func<uint, bool, string, IntPtr> open, string name, uint information)
    {
        var handle = open(ReadControl, false, name);
        Assert.NotEqual(IntPtr.Zero, handle);
        try
        {
            var error = GetSecurityInfo(handle, 6, information, out _, out _, out _, out _, out var descriptor);
            Assert.Equal(0u, error);
            try
            {
                Assert.True(ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, 1, information, out var text, out _));
                try
                {
                    return Marshal.PtrToStringUni(text)!;
                }
                finally
                {
                    LocalFree(text);
                }
            }
            finally
            {
                LocalFree(descriptor);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenEventW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenMutexW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(
        IntPtr handle, int objectType, uint information, out IntPtr owner, out IntPtr group,
        out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(
        IntPtr descriptor, uint revision, uint information, out IntPtr text, out uint length);
}
