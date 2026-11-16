# Canvas Countdown for Windows

Canvas Countdown keeps your next Canvas deadline in view: a countdown in the
notification area by the clock, a list and calendar of everything due, and
reminders before each deadline. This is the Windows version of the Mac app,
with the same features adapted to Windows.

## What you need

- Windows 10 (version 2004 or later) or Windows 11.
- An x64 PC (most Intel and AMD PCs) or an ARM64 PC (Snapdragon laptops, such
  as Copilot+ PCs and the Surface Pro X). To check, open **Settings → System →
  About** and look at **System type**.

Nothing else needs installing first. The installer brings everything the app
uses, and it installs for your account only, without asking for an
administrator password.

## Install

1. Open the [Releases page](https://github.com/le23967/CanvasCountdown/releases)
   and find the newest release whose name starts with **Windows**.
2. Download the installer for your PC:
   - `CanvasCountdown-Windows-<version>-x64-setup.exe` for x64 PCs
   - `CanvasCountdown-Windows-<version>-arm64-setup.exe` for ARM64 PCs
3. Run it. The installer isn't code-signed yet, so Windows may show **"Windows
   protected your PC"**. Choose **More info**, then **Run anyway**.

The app installs to `%LOCALAPPDATA%\Programs\Canvas Countdown` and opens when
setup finishes.

## First steps

### Connect your Canvas calendar

In Canvas, open **Calendar** and choose **Calendar Feed** (bottom right).
Copy the link, then in Canvas Countdown choose **Add → Import Canvas Feed…**
(or **Settings → Canvas Calendar Feed**) and paste it. Before anything is
saved, you see every event the feed contains and choose which ones to import.
After that, Canvas Countdown refreshes the feed by itself, every six hours by
default.

The feed link works like a password: anyone who has it can read your
calendar. Canvas Countdown keeps it in Windows Credential Manager and never
writes it to its settings file or to diagnostics.

### Keep the countdown in view

The number of days to your next deadline appears as an icon in the
notification area by the clock. Point at it to see what is due. Click it to
open the window, or right-click it to refresh or quit.

Windows 11 hides new icons behind the **^** arrow by the clock. To keep the
countdown visible, drag it from there onto the taskbar, or open **Settings →
Personalisation → Taskbar → Other system tray icons** and turn on Canvas
Countdown. While the window is open, its taskbar button shows the number too.

Closing the window doesn't quit the app: it keeps counting down and
refreshing in the notification area. To quit, right-click the icon and choose
**Quit**. You can change this under **Settings → System**, where you can also
start Canvas Countdown when you sign in.

### Reminders

Reminders arrive as Windows notifications, even while the app isn't running.
Choose when they arrive under **Settings → Notifications**. Click a reminder
to open its event. If none arrive, check that notifications for Canvas
Countdown are on in Windows **Settings → System → Notifications**, and that
Do Not Disturb or Focus isn't holding them.

## Everyday use

- **Add → Add Manual Event…** (Ctrl+N) adds something that isn't in Canvas.
  Anything you add can be taken back with **Undo Adding** in the toolbar
  (Ctrl+Z).
- **Search** (Ctrl+F) looks through every event, including completed ones.
- **Filter** shows one course at a time, or brings back completed and
  ignored events.
- **Calendar** has day, week, month and year views (Ctrl+1 to Ctrl+4). **Go
  to Date…** (Ctrl+Shift+T) accepts dates such as "15 December".
- Each event's **Actions** button marks it completed or ignored, or gives it a
  label.
- **Add → Import from Screenshot…** (Ctrl+Shift+I) reads deadlines from a
  screenshot with Windows' own text recognition, on your PC. You check every
  row before anything is saved, and the whole import can be undone. Text
  recognition needs a language with its optical character recognition
  feature, which English usually has. If nothing can be read, add one under
  **Settings → Time & language → Language & region**.
- **Settings → Courses** removes a course you don't take. Its Canvas events
  are deleted and the feed stops importing it. Events you added yourself stay.
  **Allow Again** brings the course back at the next refresh.

## The optional assistant

The assistant answers questions about your deadlines and turns a sentence
such as "essay due Friday at 5" into events. It never saves anything until
you have reviewed it. It is off until you turn it on under **Settings → AI
Assistant**, and it can run either:

- **on your PC**, with [Ollama](https://ollama.com). Settings shows the
  `winget` command that installs it and lets you download a model. Nothing
  leaves your computer.
- **with an online service** of your choice, using your own API key. The
  key is kept in Windows Credential Manager. Your questions and the deadlines
  they concern are sent to that service.

## Updates

Canvas Countdown checks GitHub for a new version once a day, and you can
check yourself under **Settings → Updates**. When there's an update, a banner
offers **Download and Install**. The app downloads the installer, makes sure
it's exactly the file that was published (same size and SHA-256 checksum),
installs it and reopens. If anything goes wrong partway through, the
installer restores the version you had, and the app tells you so.

## Your data

Everything stays on your PC:

| What | Where |
| --- | --- |
| Events and settings | `%LOCALAPPDATA%\CanvasCountdown` |
| Canvas feed link | Credential Manager, `CanvasCountdown:canvas-calendar-feed` |
| Assistant API keys | Credential Manager, `CanvasCountdown:assistant-api-key.<id>` |

The app only connects to your Canvas feed, to GitHub for update checks, and
to an assistant service if you set one up. It sends no analytics or usage
data. **Settings → Data & Privacy → Export Redacted Diagnostics…** saves a
report you can share when something goes wrong. It contains counts and
states, not your feed link or event descriptions.

## Uninstall

Open **Settings → Apps → Installed apps**, find Canvas Countdown and choose
**Uninstall**. Any scheduled reminders and the start-at-sign-in entry are
removed with it.

Your events and settings stay behind, in case you reinstall. To remove
everything, choose **Settings → Data & Privacy → Reset Local Data…** in Canvas
Countdown before uninstalling. That deletes your events, settings and saved
feed link. Then delete any assistant keys in **Credential Manager → Windows
Credentials** (the entries starting with `CanvasCountdown:`).

## Differences from the Mac version

- The countdown lives in the notification area, not the Dock. A tray icon
  only has room for the number, so the Mac's label and icon-size settings
  don't apply.
- Updates install themselves and reopen the app. On the Mac you open the
  downloaded disk image yourself.
- Screenshot text recognition uses Windows' built-in engine instead of
  Apple's.

## Building from source

The Windows version lives entirely in `windows/`.

- The core (feed parsing, the database, countdown and calendar rules,
  reminders, updates, the assistant, and the view model) is plain .NET 10 and
  builds and tests on any OS:

  ```
  dotnet test windows/tests/CanvasCountdown.Core.Tests
  ```

- The app (`windows/src/CanvasCountdown.App`, WinUI 3) builds on Windows with
  the .NET 10 SDK:

  ```
  dotnet publish windows/src/CanvasCountdown.App -c Release -r win-x64 -p:Platform=x64
  ```

- `windows/scripts/build-release.ps1 -Version 1.0.0` builds both installers.
  It needs [Inno Setup 6](https://jrsoftware.org/isinfo.php) and prints each
  installer's SHA-256.

`.github/workflows/windows.yml` runs all of this on GitHub's Windows machines
(x64 and ARM64). Besides the tests, it builds the app, starts it, and walks
through it as a user would. It also installs a release, updates it, makes an
update fail partway to check that the previous version is restored, and
uninstalls it. The coursework in its screenshots is invented.
