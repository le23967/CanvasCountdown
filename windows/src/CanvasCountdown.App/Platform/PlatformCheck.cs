using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace CanvasCountdown.App.Platform;

/// <summary>
/// Exercises the Windows services the app depends on, for CI: scheduled
/// notifications, text recognition, Credential Manager, the startup entry and
/// the tray icon.
/// </summary>
/// <remarks>
/// These are the parts the core tests cannot reach and the UI tour does not
/// touch, because an isolated launch replaces them all. Run with
/// <c>--platform-check &lt;report file&gt;</c>, isolated only: it schedules a
/// reminder under the app's own name and briefly writes a credential and a
/// startup entry, and on somebody's own PC that would disturb their real ones.
/// Everything it adds it removes again. Exit code 0 means every check passed.
/// </remarks>
internal static class PlatformCheck
{
    public static int Run(string? reportPath)
    {
        var report = new StringBuilder("Canvas Countdown platform check\n");
        if (!AppEnvironment.IsIsolatedLaunch || string.IsNullOrWhiteSpace(reportPath))
        {
            return 2;
        }

        var failures = 0;
        // On a worker, not the UI thread's STA: nothing here needs a window,
        // and waiting on Windows' async calls from an STA can stall.
        Task.Run(async () =>
        {
            failures += await CheckAsync("Scheduled reminders", report, CheckRemindersAsync);
            failures += await CheckAsync("Credential Manager", report, CheckCredentialsAsync);
            failures += await CheckAsync("Startup entry", report, () => Task.FromResult(CheckStartupEntry()));
            failures += await CheckAsync("Tray icon", report, () => Task.FromResult(CheckTrayIcon()));
            failures += await CheckAsync("Text recognition", report, CheckTextRecognitionAsync);
        }).GetAwaiter().GetResult();

        report.AppendLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
        File.WriteAllText(reportPath, report.ToString());
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> CheckAsync(string name, StringBuilder report, Func<Task<string>> check)
    {
        try
        {
            report.AppendLine($"ok    {name}: {await check()}");
            return 0;
        }
        catch (Exception exception)
        {
            report.AppendLine($"FAIL  {name}: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static void Expect(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"expected {description}");
        }
    }

    /// <summary>The same path a real launch takes: register the app's name, then schedule through it.</summary>
    private static async Task<string> CheckRemindersAsync()
    {
        AppIdentity.EnsureRegistered();
        AppIdentity.ApplyToProcess();
        var reminders = new ToastReminders();
        var status = await reminders.AuthorizationStatusAsync();
        var now = DateTimeOffset.Now;
        var request = new ReminderNotificationRequest
        {
            Identifier = $"platform-check-{Guid.NewGuid():N}",
            AssignmentId = Guid.NewGuid(),
            OffsetMinutes = 60,
            FireDate = now.AddHours(2),
            TriggerDate = now.AddHours(1),
            Title = "Platform check",
            Body = "Scheduled by the platform check and removed straight away.",
        };
        await reminders.ReplacePendingAsync([request]);
        var scheduled = reminders.ScheduledCount;
        await reminders.RemoveAllPendingAsync();
        var left = reminders.ScheduledCount;
        Expect(scheduled == 1, $"one reminder in Windows' schedule (found {scheduled})");
        Expect(left == 0, $"the schedule empty again (found {left})");
        return $"notifications {status}; scheduled 1, removed it";
    }

    private static async Task<string> CheckCredentialsAsync()
    {
        var store = new CredentialManagerSecretStore();
        const string account = "platform-check";
        const string secret = "https://canvas.example.edu/feeds/calendar.ics?feed=Prüfung-✓";
        await store.WriteAsync(account, secret);
        var read = await store.ReadAsync(account);
        await store.DeleteAsync(account);
        var afterDelete = await store.ReadAsync(account);
        Expect(read == secret, "the secret to read back exactly as written");
        Expect(afterDelete is null, "nothing left once deleted");
        return "wrote, read back and deleted a secret";
    }

    private static string CheckStartupEntry()
    {
        var startup = new StartupRegistration();
        var before = startup.IsEnabled;
        startup.SetEnabled(true);
        var enabled = startup.IsEnabled;
        startup.SetEnabled(false);
        var disabled = !startup.IsEnabled;
        if (before)
        {
            startup.SetEnabled(true);
        }
        Expect(enabled, "the startup entry to read as on once written");
        Expect(disabled, "the startup entry to read as off once removed");
        return "turned on and off again";
    }

    private static string CheckTrayIcon()
    {
        var sizes = new[] { 16, 20, 24, 32 };
        foreach (var size in sizes)
        {
            var icon = TileRenderer.RenderIcon(DockAppearance.Defaults, 7, null, size);
            Expect(icon != IntPtr.Zero, $"an icon at {size} px");
            DestroyIcon(icon);
        }
        var png = TileRenderer.RenderPng(DockAppearance.Defaults, 12, null, 32);
        Expect(png.Length > 100, "a PNG preview");
        return $"drew icons at {string.Join(", ", sizes)} px";
    }

    /// <summary>
    /// Reads an invented deadline back out of a picture drawn here. A PC with
    /// no recognition language installed is reported rather than failed: that
    /// is a setting on the machine, and the app explains it when it happens.
    /// </summary>
    private static async Task<string> CheckTextRecognitionAsync()
    {
        var languages = Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag).ToArray();
        if (languages.Length == 0)
        {
            return "skipped: this PC has no text recognition language installed";
        }

        byte[] image;
        using (var bitmap = new Bitmap(1200, 240))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", 30, FontStyle.Regular, GraphicsUnit.Pixel);
            graphics.DrawString("Research Proposal  COMP2050", font, Brushes.Black, 40, 40);
            graphics.DrawString("Due 15 December 2026 at 11:59 PM", font, Brushes.Black, 40, 130);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            image = stream.ToArray();
        }

        var observations = await new WindowsOcrRecognizer().RecognizeTextAsync(
            new ScreenshotSource { DisplayName = "platform-check.png", ImageData = image });
        var text = string.Join(" | ", observations.Select(observation => observation.Text));
        Expect(text.Contains("December", StringComparison.OrdinalIgnoreCase), $"to read the date (read: {text})");
        Expect(text.Contains("Proposal", StringComparison.OrdinalIgnoreCase), $"to read the title (read: {text})");
        return $"languages {string.Join(", ", languages)}; read \"{text}\"";
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
