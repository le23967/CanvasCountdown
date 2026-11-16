using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace CanvasCountdown.UiTour;

/// <summary>
/// A walk through the whole app, as a first-time user would take it, with a
/// picture of the window at every step and a report of what worked.
/// </summary>
/// <remarks>
/// It is the part of testing that the core tests cannot do: that the window
/// opens, that every section draws, that a click on Add really adds an event
/// and Undo really takes it away. Each step is independent, so one broken
/// screen does not hide the state of the rest.
/// </remarks>
internal static class Program
{
    private static readonly List<(string Name, bool Passed, string Detail)> Results = [];
    private static string outputDirectory = ".";
    private static int appProcessId;

    private static int Main(string[] args)
    {
        var appPath = Argument(args, "--app") ?? throw new ArgumentException("--app <path to CanvasCountdown.exe> is required");
        outputDirectory = Argument(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, "ui-tour");
        Directory.CreateDirectory(outputDirectory);

        var start = new ProcessStartInfo(appPath) { UseShellExecute = false };
        start.Environment["CANVAS_COUNTDOWN_ISOLATED_LAUNCH"] = "1";
        start.Environment["CANVAS_COUNTDOWN_DEMO_DATA"] = "1";

        using var automation = new UIA3Automation();
        var app = Application.Launch(start);
        var processId = app.ProcessId;
        appProcessId = processId;
        try
        {
            var window = Retry.WhileNull(() => app.GetMainWindow(automation, TimeSpan.FromSeconds(5)), TimeSpan.FromSeconds(60)).Result
                ?? throw new InvalidOperationException("The main window never appeared.");
            window.SetForeground();
            Thread.Sleep(2500);

            Step("01-upcoming", window, () => Expect(Find(window, "NextDeadlineCard") is not null, "the next-deadline card is shown"));
            Step("02-all-events", window, () => Navigate(window, "NavAllEvents", "All Events"));
            Step("03-calendar-month", window, () => Navigate(window, "NavCalendar", "Calendar"));
            Step("04-calendar-week", window, () => Click(window, "ScaleWeek"));
            Step("05-calendar-day", window, () => Click(window, "ScaleDay"));
            Step("06-calendar-year", window, () => Click(window, "ScaleYear"));
            Step("07-go-to-date", window, () =>
            {
                Click(window, "ScaleMonth");
                Click(window, "GoToDate");
                var entry = Wait(() => Find(window, "DateEntry") ?? FindOnDesktop(automation, "DateEntry"), "the Go to Date box");
                // Through UI Automation rather than typed keys, which go to
                // whatever has focus — on a fresh runner, sometimes the Start menu.
                SetText(entry, "15 December");
                Thread.Sleep(600);
            }, captureScreen: true);
            Step("08-go-to-date-result", window, () =>
            {
                Invoke(Wait(() => Find(window, "GoToDateGo") ?? FindOnDesktop(automation, "GoToDateGo"), "the Go button"));
                Thread.Sleep(800);
                var title = Find(window, "CalendarTitle")?.Name ?? string.Empty;
                Expect(title.Contains("December", StringComparison.OrdinalIgnoreCase), $"the calendar moved to December (title: {title})");
            });
            Step("09-completed", window, () => Navigate(window, "NavCompleted", "Completed"));
            Step("10-settings", window, () => NavigateToSettings(window));
            // Further down Settings: the tray previews, labels, the assistant, system and updates.
            Step("10b-settings-notifications", window, () => ScrollSettings(window, 15));
            Step("10c-settings-appearance", window, () => ScrollSettings(window, 30));
            Step("10d-settings-courses", window, () => ScrollSettings(window, 50));
            Step("10e-settings-assistant", window, () => ScrollSettings(window, 70));
            Step("10f-settings-end", window, () => ScrollSettings(window, 100));
            Step("11-upcoming-again", window, () =>
            {
                Navigate(window, "NavUpcoming", "Upcoming");
                // Every confirmation so far has had time to go, bar and all.
                Thread.Sleep(2500);
                Expect(Find(window, "StatusBar") is null or { IsOffscreen: true }, "no status bar is left on screen once its message has gone");
            });

            // The row's Actions menu: complete an event, find it under
            // Completed, and put it back.
            Step("11b-mark-complete", window, () =>
            {
                Invoke(Wait(() => FindByName(window, "Actions for Quiz 3", ControlType.Button), "the Actions button for Quiz 3"));
                Invoke(Wait(() => FindMenuItem(automation, window, "Mark Complete"), "Mark Complete in the Actions menu"));
                Thread.Sleep(800);
                Expect(!ListContains(window, "Quiz 3"), "Quiz 3 leaves Upcoming once it is complete");
            });
            Step("11c-completed", window, () =>
            {
                Navigate(window, "NavCompleted", "Completed");
                Expect(ListContains(window, "Quiz 3"), "Quiz 3 is listed under Completed");
                Invoke(Wait(() => FindByName(window, "Actions for Quiz 3", ControlType.Button), "the Actions button for Quiz 3"));
                Invoke(Wait(() => FindMenuItem(automation, window, "Mark Incomplete"), "Mark Incomplete in the Actions menu"));
                Thread.Sleep(800);
                Navigate(window, "NavUpcoming", "Upcoming");
                Expect(ListContains(window, "Quiz 3"), "Quiz 3 is back in Upcoming");
            });

            Step("12-add-manual-event", window, () =>
            {
                Click(window, "Add");
                var menuItem = Wait(() => FindOnDesktop(automation, "AddManualEvent"), "the Add menu");
                menuItem.Click();
                var title = Wait(() => Find(window, "ManualTitle"), "the event name box");
                SetText(title, "Practice exam (tour)");
                Thread.Sleep(400);
            });
            Step("13-event-added-with-undo", window, () =>
            {
                Wait(() => FindByName(window, "Add Event", ControlType.Button), "the Add Event button").AsButton().Invoke();
                Wait(() => Find(window, "UndoToast"), "the undo offer");
                Expect(ListContains(window, "Practice exam (tour)"), "the new event is in the list");
                Expect(Find(window, "UndoAddition") is { IsOffscreen: false }, "the toolbar offers Undo Adding");
            });
            Step("14-undone", window, () =>
            {
                Click(window, "UndoToastUndo");
                Thread.Sleep(1200);
                Expect(!ListContains(window, "Practice exam (tour)"), "the event is gone after Undo");
            });

            Step("15-search", window, () =>
            {
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_F);
                var field = Wait(() => Find(window, "SearchField"), "the search box");
                SetText(field, "STAT");
                Thread.Sleep(800);
                var results = Find(window, "SearchResults");
                Expect(results is not null && results.FindAllChildren().Length > 0, "search found STAT1010 work");
            });
            Step("16-search-closed", window, () =>
            {
                Keyboard.Press(VirtualKeyShort.ESCAPE);
                Thread.Sleep(500);
                Expect(Find(window, "SearchPanel") is null or { IsOffscreen: true }, "Esc closes search");
            });

            Step("17-filter-menu", window, () => Click(window, "Filter"), captureScreen: true);
            Step("18-assistant", window, () =>
            {
                Keyboard.Press(VirtualKeyShort.ESCAPE);
                Click(window, "Assistant");
                Wait(() => Find(window, "AssistantComposer"), "the assistant's box");
            });
            Step("19-assistant-answer", window, () =>
            {
                var composer = Find(window, "AssistantComposer")!;
                SetText(composer, "What is most urgent?");
                Click(window, "AssistantSend");
                var conversation = Wait(() => Find(window, "AssistantConversation"), "the conversation");
                // The demo assistant answers from the invented coursework.
                var answer = Retry.WhileNull(
                    () => conversation.FindAllDescendants().FirstOrDefault(element => element.Name?.StartsWith("Most urgent", StringComparison.Ordinal) == true),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromMilliseconds(200)).Result;
                Expect(answer is not null, "the conversation shows the assistant's answer");
            });
        }
        catch (Exception exception)
        {
            Results.Add(("tour", false, exception.ToString()));
        }
        finally
        {
            // Closing the window only puts the app in the notification area,
            // so it is ended by its process id. FlaUI may already have let go
            // of its own handle to the process by then.
            try
            {
                app.Close(killIfCloseFails: false);
            }
            catch (Exception)
            {
            }
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }

        WriteReport();
        return Results.All(result => result.Passed) ? 0 : 1;
    }

    // ------------------------------------------------------------------
    // Steps

    private static void Step(string name, Window window, Action action, bool captureScreen = false)
    {
        try
        {
            action();
            Thread.Sleep(700);
            Results.Add((name, true, string.Empty));
        }
        catch (Exception exception)
        {
            Results.Add((name, false, exception.Message));
        }
        finally
        {
            try
            {
                if (captureScreen)
                {
                    CaptureAppWindows(Path.Combine(outputDirectory, name + ".png"));
                }
                else
                {
                    CaptureWindow(window, Path.Combine(outputDirectory, name + ".png"));
                }
            }
            catch (Exception exception)
            {
                Results.Add((name + " (picture)", false, exception.Message));
            }
            // Written as it goes, so a tour that dies part-way still says how far it got.
            WriteReport();
        }
    }

    private static void Navigate(Window window, string automationId, string expectedTitle)
    {
        Click(window, automationId);
        Thread.Sleep(600);
        var title = Find(window, "SectionTitle")?.Name;
        Expect(title == expectedTitle, $"the section title reads \"{expectedTitle}\" (it reads \"{title}\")");
    }

    private static void NavigateToSettings(Window window)
    {
        var settings = Find(window, "SettingsItem") ?? FindByName(window, "Settings", ControlType.ListItem)
            ?? throw new InvalidOperationException("No Settings item in the navigation pane.");
        Invoke(settings);
        Thread.Sleep(800);
        Expect(Find(window, "SectionTitle")?.Name == "Settings", "Settings opened");
    }

    private static void ScrollSettings(Window window, double percent)
    {
        var page = Wait(() => Find(window, "SettingsPage"), "the Settings page");
        Expect(page.Patterns.Scroll.IsSupported, "the Settings page scrolls");
        // The sections are drawn once the page has loaded; scrolling before
        // that measures a page a fraction of its real length.
        Retry.WhileFalse(() => page.Patterns.Scroll.Pattern.VerticalViewSize.Value < 60, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200));
        page.Patterns.Scroll.Pattern.SetScrollPercent(-1, percent);
        Thread.Sleep(600);
    }

    private static void Click(Window window, string automationId)
        => Invoke(Wait(() => Find(window, automationId), automationId));

    /// <summary>Presses a control the way assistive technology would, so nothing on top of the window can intercept it.</summary>
    private static void Invoke(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
        }
        else if (element.Patterns.SelectionItem.IsSupported)
        {
            element.Patterns.SelectionItem.Pattern.Select();
        }
        else
        {
            element.Click();
        }
        Thread.Sleep(500);
    }

    private static void SetText(AutomationElement element, string text)
    {
        if (element.Patterns.Value.IsSupported)
        {
            element.Patterns.Value.Pattern.SetValue(text);
        }
        else
        {
            element.AsTextBox().Enter(text);
        }
    }

    private static bool ListContains(Window window, string title)
        => Find(window, "AssignmentList")?.FindAllDescendants().Any(element => element.Name?.Contains(title, StringComparison.Ordinal) == true) == true;

    private static AutomationElement? Find(AutomationElement root, string automationId)
        => root.FindFirstDescendant(condition => condition.ByAutomationId(automationId));

    private static AutomationElement? FindByName(AutomationElement root, string name, ControlType type)
        => root.FindFirstDescendant(condition => condition.ByName(name).And(condition.ByControlType(type)));

    /// <summary>A menu item, in the window or in the popup window a menu can open in.</summary>
    private static AutomationElement? FindMenuItem(UIA3Automation automation, Window window, string name)
        => FindByName(window, name, ControlType.MenuItem)
            ?? automation.GetDesktop().FindFirstDescendant(condition => condition.ByName(name).And(condition.ByControlType(ControlType.MenuItem)));

    /// <summary>Menus and flyouts can open in windows of their own.</summary>
    private static AutomationElement? FindOnDesktop(UIA3Automation automation, string automationId)
        => automation.GetDesktop().FindFirstDescendant(condition => condition.ByAutomationId(automationId));

    private static AutomationElement Wait(Func<AutomationElement?> find, string what)
        => Retry.WhileNull(find, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200)).Result
            ?? throw new InvalidOperationException($"Could not find {what}.");

    private static void Expect(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Expected {description}.");
        }
    }

    // ------------------------------------------------------------------
    // Pictures

    /// <summary>
    /// The window alone, drawn by the window itself, so nothing else on the
    /// screen is ever in the picture and another window on top cannot spoil it.
    /// </summary>
    private static void CaptureWindow(Window window, string path)
    {
        var handle = window.Properties.NativeWindowHandle.Value;
        if (DwmGetWindowAttribute(handle, 9, out var frame, Marshal.SizeOf<Rect>()) != 0)
        {
            GetWindowRect(handle, out frame);
        }
        GetWindowRect(handle, out var outer);
        var width = Math.Max(1, outer.Right - outer.Left);
        var height = Math.Max(1, outer.Bottom - outer.Top);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            PrintWindow(handle, dc, 2);
            graphics.ReleaseHdc(dc);
        }
        // Trim the invisible resize border Windows adds round every window.
        var crop = new Rectangle(frame.Left - outer.Left, frame.Top - outer.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        if (crop.Width > 0 && crop.Height > 0 && crop.Right <= width && crop.Bottom <= height)
        {
            using var cropped = bitmap.Clone(crop, bitmap.PixelFormat);
            cropped.Save(path, ImageFormat.Png);
        }
        else
        {
            bitmap.Save(path, ImageFormat.Png);
        }
    }

    /// <summary>
    /// For menus, which open as windows of their own and are not in the main
    /// window's own picture: the area covered by the app's visible windows,
    /// and nothing else on the screen.
    /// </summary>
    private static void CaptureAppWindows(string path)
    {
        var bounds = Rectangle.Empty;
        EnumWindows((handle, _) =>
        {
            if (IsWindowVisible(handle) && GetWindowThreadProcessId(handle, out var owner) != 0 && owner == appProcessId)
            {
                if (DwmGetWindowAttribute(handle, 9, out var frame, Marshal.SizeOf<Rect>()) != 0)
                {
                    GetWindowRect(handle, out frame);
                }
                var rectangle = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
                if (rectangle.Width > 1 && rectangle.Height > 1)
                {
                    bounds = bounds.IsEmpty ? rectangle : Rectangle.Union(bounds, rectangle);
                }
            }
            return true;
        }, IntPtr.Zero);
        if (bounds.IsEmpty)
        {
            throw new InvalidOperationException("The app has no visible window to photograph.");
        }
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
        }
        bitmap.Save(path, ImageFormat.Png);
    }

    private static void WriteReport()
    {
        var report = new StringBuilder();
        report.AppendLine("# UI tour");
        report.AppendLine();
        foreach (var (name, passed, detail) in Results)
        {
            report.AppendLine($"- {(passed ? "ok  " : "FAIL")} {name}{(string.IsNullOrEmpty(detail) ? string.Empty : " — " + detail.ReplaceLineEndings(" "))}");
        }
        File.WriteAllText(Path.Combine(outputDirectory, "report.md"), report.ToString());
        Console.WriteLine(report.ToString());
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);
}
