using System.Globalization;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// The language is applied while the app starts up inside an async method. A culture set there only lives in
/// that method's execution context and is put back when the method returns to the message loop, so the
/// thread that later formats sizes would fall back to the Windows language: English UI with a German
/// decimal comma. The chosen language has to hold for every thread, whatever context applied it.
/// </summary>
[Collection("UiLanguage")]
public class UiCultureOnUiThreadTests
{
    private static async Task ApplyLikeStartupDoes(string language)
    {
        LocalizationService.Instance.Apply(language);
        await Task.Yield();
    }

    /// <summary>Runs on a thread of its own that starts in the Windows culture (German), the way the UI
    /// thread looks before any language is applied; leaves English applied afterwards like the other culture tests.</summary>
    private static T OnFreshGermanThread<T>(Func<T> body)
    {
        // A new thread has no culture of its own: it takes the process default, which here stands in for the
        // Windows language the real UI thread starts with.
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("de-DE");
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        try
        {
            thread.Start();
            thread.Join();
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = null;
            LocalizationService.Instance.Apply("en");
        }

        if (failure is not null)
            throw new InvalidOperationException("Thread body failed.", failure);

        return result!;
    }

    [Fact]
    public void A_size_reads_with_a_period_after_english_was_applied_inside_an_async_startup()
    {
        var text = OnFreshGermanThread(() =>
        {
            _ = ApplyLikeStartupDoes("en");
            return ByteSize.Format(2L * 1024 * 1024 * 1024);
        });

        Assert.Equal("2.0 GB", text);
    }

    [Fact]
    public void A_size_reads_with_a_comma_after_german_was_applied_inside_an_async_startup()
    {
        var text = OnFreshGermanThread(() =>
        {
            _ = ApplyLikeStartupDoes("de");
            return ByteSize.Format(2L * 1024 * 1024 * 1024);
        });

        Assert.Equal("2,0 GB", text);
    }

    [Fact]
    public void A_pool_thread_formats_in_the_chosen_language_too()
    {
        var text = OnFreshGermanThread(() =>
        {
            _ = ApplyLikeStartupDoes("en");
            return Task.Run(() => ByteSize.Format(2L * 1024 * 1024 * 1024)).GetAwaiter().GetResult();
        });

        Assert.Equal("2.0 GB", text);
    }
}
