using System.IO;
using System.IO.Pipes;
using System.Text;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

public class SingleInstanceServiceTests
{
    [Fact]
    public void The_pipe_name_carries_the_session_and_the_user()
    {
        var name = SingleInstanceService.BuildPipeName(2, "S-1-5-21-111-222-333-1001");

        Assert.Equal("UltimateUpdater-SingleInstance-Pipe-2-S-1-5-21-111-222-333-1001", name);
    }

    [Fact]
    public void The_mutex_name_carries_the_user_and_never_a_path_character()
    {
        var first = SingleInstanceService.BuildMutexName("S-1-5-21-1-2-3-1001");

        Assert.StartsWith("Local\\", first);
        Assert.EndsWith("S-1-5-21-1-2-3-1001", first);
        Assert.NotEqual(first, SingleInstanceService.BuildMutexName("S-1-5-21-1-2-3-1002"));
        var hostile = SingleInstanceService.BuildMutexName("S-1\\..\\x/y:z")[6..];
        Assert.DoesNotContain('/', hostile);
        Assert.DoesNotContain('\\', hostile);
        Assert.DoesNotContain(':', hostile);
    }

    [Fact]
    public void Two_sessions_or_two_users_never_share_a_pipe_name()
    {
        var first = SingleInstanceService.BuildPipeName(1, "S-1-5-21-1-2-3-1001");

        Assert.NotEqual(first, SingleInstanceService.BuildPipeName(2, "S-1-5-21-1-2-3-1001"));
        Assert.NotEqual(first, SingleInstanceService.BuildPipeName(1, "S-1-5-21-1-2-3-1002"));
    }

    [Fact]
    public void A_pipe_name_never_takes_a_character_a_path_could_use()
    {
        var name = SingleInstanceService.BuildPipeName(1, "S-1-5\\..\\evil/x:y");

        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('.', name);
    }

    [Fact]
    public void A_squatted_mutex_name_starts_the_app_without_the_guard_and_logs_a_warning()
    {
        var lines = new List<string>();
        SingleInstanceService.CreateMutexForTest.Value = _ => throw new UnauthorizedAccessException("denied");
        try
        {
            Assert.True(SingleInstanceService.TryAcquire(new LoggingService(lines.Add)));
            Assert.Contains(lines, line => line.Contains("held by another account", StringComparison.Ordinal));

            SingleInstanceService.Release(); // safe with no mutex held
        }
        finally
        {
            SingleInstanceService.CreateMutexForTest.Value = null;
        }
    }

    [Fact]
    public void A_name_held_by_another_object_type_starts_the_app_without_the_guard()
    {
        var lines = new List<string>();
        SingleInstanceService.CreateMutexForTest.Value = _ => throw new WaitHandleCannotBeOpenedException("event");
        try
        {
            Assert.True(SingleInstanceService.TryAcquire(new LoggingService(lines.Add)));
            Assert.Contains(lines, line => line.Contains("held by another account", StringComparison.Ordinal));

            SingleInstanceService.Release();
        }
        finally
        {
            SingleInstanceService.CreateMutexForTest.Value = null;
        }
    }

    [Fact]
    public void A_mutex_that_already_exists_means_another_instance_runs()
    {
        SingleInstanceService.CreateMutexForTest.Value = _ => (new Mutex(false), false);
        SingleInstanceService.ReadMutexOwnerForTest.Value = _ => System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        try
        {
            Assert.False(SingleInstanceService.TryAcquire(new LoggingService(_ => { })));
            SingleInstanceService.Release();
        }
        finally
        {
            SingleInstanceService.CreateMutexForTest.Value = null;
            SingleInstanceService.ReadMutexOwnerForTest.Value = null;
        }
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-500")]
    [InlineData(null)]
    public void An_existing_mutex_of_a_foreign_or_unreadable_owner_starts_the_app_without_the_guard(string? owner)
    {
        var lines = new List<string>();
        SingleInstanceService.CreateMutexForTest.Value = _ => (new Mutex(false), false);
        SingleInstanceService.ReadMutexOwnerForTest.Value = _ => owner;
        try
        {
            Assert.True(SingleInstanceService.TryAcquire(new LoggingService(lines.Add)));
            Assert.Contains(lines, line => line.Contains("held by another account", StringComparison.Ordinal));
            SingleInstanceService.Release();
        }
        finally
        {
            SingleInstanceService.CreateMutexForTest.Value = null;
            SingleInstanceService.ReadMutexOwnerForTest.Value = null;
        }
    }

    [Fact]
    public void The_owner_read_route_recognises_a_mutex_created_by_this_user()
    {
        var name = @"Local\uu-owner-test-" + Guid.NewGuid().ToString("N");
        using var mutex = new Mutex(false, name);

        var owner = SingleInstanceService.ReadMutexOwnerSid(name);

        Assert.NotNull(owner);
        Assert.True(SingleInstanceService.IsOwnInstanceOwner(owner));
    }

    [Fact]
    public async Task A_complete_short_line_is_returned()
    {
        using var reader = new StringReader("focus\r\nmore");

        Assert.Equal("focus", await SingleInstanceService.ReadRequestAsync(reader, SingleInstanceService.MaxRequestChars, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_line_longer_than_the_cap_is_refused()
    {
        using var reader = new StringReader(new string('x', SingleInstanceService.MaxRequestChars + 1) + "\n");

        Assert.Null(await SingleInstanceService.ReadRequestAsync(reader, SingleInstanceService.MaxRequestChars, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_line_of_exactly_the_cap_is_accepted()
    {
        var text = new string('x', SingleInstanceService.MaxRequestChars);
        using var reader = new StringReader(text + "\n");

        Assert.Equal(text, await SingleInstanceService.ReadRequestAsync(reader, SingleInstanceService.MaxRequestChars, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task An_empty_connection_yields_nothing()
    {
        using var reader = new StringReader(string.Empty);

        Assert.Null(await SingleInstanceService.ReadRequestAsync(reader, SingleInstanceService.MaxRequestChars, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_silent_client_is_dropped_after_the_timeout()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.In);
        using var client = new AnonymousPipeClientStream(PipeDirection.Out, server.ClientSafePipeHandle);
        using var reader = new StreamReader(server, Encoding.UTF8);

        var started = DateTime.UtcNow;
        var line = await SingleInstanceService.ReadRequestAsync(reader, SingleInstanceService.MaxRequestChars, TimeSpan.FromMilliseconds(200));

        Assert.Null(line);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "the read must give up near its timeout");
    }

    [Fact]
    public void The_server_waits_at_most_two_seconds_for_a_request_of_at_most_64_characters()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), SingleInstanceService.RequestTimeout);
        Assert.Equal(64, SingleInstanceService.MaxRequestChars);
    }

    [Fact]
    public async Task A_second_launch_reaches_the_listener_over_the_pipe_and_gets_an_answer()
    {
        var name = SingleInstanceService.BuildPipeName(9999, "S-test-" + Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource();
        var focused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            SingleInstanceService.StartListening(() => focused.TrySetResult(), name, stop.Token);

            Assert.True(await SingleInstanceService.NotifyExistingInstanceAsync(name));
            await focused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            stop.Cancel();
        }
    }

    [Fact]
    public async Task An_oversized_request_gets_no_answer_and_does_not_raise_the_window()
    {
        var name = SingleInstanceService.BuildPipeName(9998, "S-test-" + Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource();
        var focused = 0;

        try
        {
            SingleInstanceService.StartListening(() => Interlocked.Increment(ref focused), name, stop.Token);

            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(3000);
            var writer = new StreamWriter(client) { AutoFlush = true };   // not disposed: a failed write would make its flush throw again
            using var reader = new StreamReader(client, leaveOpen: true);
            string? answer = null;
            try
            {
                await writer.WriteLineAsync(new string('x', 200));

                using var timeout = new CancellationTokenSource(1500);
                answer = await reader.ReadLineAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // The server drops the connection as soon as it has seen too much, which can surface here as
                // a broken pipe on either call; both mean "no answer".
            }

            Assert.NotEqual("focused", answer);
            Assert.Equal(0, Volatile.Read(ref focused));
        }
        finally
        {
            stop.Cancel();
        }
    }
}
