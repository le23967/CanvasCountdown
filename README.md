# Canvas Countdown

**Website: https://le23967.github.io/CanvasCountdown/**

A native macOS deadline manager for Canvas LMS. It keeps your assignment
deadlines on your Mac and draws the nearest one as a large calendar-day
countdown in the Dock icon, so the number of days left is visible without
opening anything.

Built with Swift 6, SwiftUI, AppKit, SwiftData, Keychain Services, URLSession,
Vision and UserNotifications. No account, no analytics, no application server,
no paid dependency.

## Features

- **Canvas Calendar Feed import** — paste your feed URL once; the app refreshes
  on a schedule while it is running.
- **Screenshot import with mandatory review** — recognition runs locally with
  Apple Vision, and nothing is saved until you have checked it.
- **AI deadline Assistant** — ask what is most urgent, or write a task in a
  sentence, give it a course and a label, and review the draft before it is
  saved. One click undoes everything a batch just added. Optional, and local by
  default.
- **Any AI service, saved by name** — anything speaking the common
  chat-completions API works, including a model running locally through Ollama.
  Settings suggests the major services and fills in their address for you. Keep
  several models, each with its own key in the Keychain, and switch between them
  from the toolbar.
- **11:59 PM due times** — a Canvas deadline that arrives as midnight is shown
  as the end of that day, which is what it means. The day and the countdown
  never move, and it can be turned off.
- **Labels** — a name and a colour of your own (Important, Personal, Society,
  whatever you need), shown in the list and on the calendar.
- **Calendar** — day, week, month and year views, with ⌘1–⌘4, ⌘T for today and
  ⌘⇧T to type a date and jump to it.
- **Notifications** — local reminders at 7 days, 3 days, 1 day and on the due
  day by default, and any schedule you add.
- **Manual events** — countdowns that have nothing to do with Canvas.
- **Course filtering** — narrow Upcoming, the nearest-deadline card and the
  Dock countdown to the courses you care about.
- **Course removal** — take an old course out for good, so the Canvas feed
  stops importing a subject you finished last year. Reversible.
- **Live Dock countdown** — the days remaining, drawn in the Dock tile while the
  app runs.
- **Custom Dock appearance** — number size and weight, background, number and
  header colours, four built-in themes, and a Chinese or English header label.
- **Reusable Dock theme presets** — save a colour scheme you like, rename it,
  duplicate it, apply it again later.

## Requirements

- macOS 14.0 (Sonoma) or later
- macOS 13 and earlier cannot run it: the app is built on SwiftData
  and Observation, which Apple ships only from macOS 14
- Apple silicon or Intel (the app ships as a universal binary)
- A Canvas Calendar Feed URL from your own institution's Canvas

## Installation

1. Download `CanvasCountdown-1.1.0.dmg` from the
   [latest release](https://github.com/le23967/CanvasCountdown/releases/latest).
2. Open the DMG and drag **Canvas Countdown** into **Applications**.
3. **This build is ad-hoc signed and is not notarized by Apple.** The first
   launch therefore needs one extra step: right-click (or Control-click) the app
   in Applications, choose **Open**, then confirm **Open** in the dialog.
   Double-clicking it the normal way will be blocked by Gatekeeper the first
   time.
4. If macOS still refuses, open **System Settings → Privacy & Security**, scroll
   to the message about Canvas Countdown, and choose **Open Anyway**.

After the first launch it opens normally like any other app.

## Canvas Feed Setup

Canvas exposes the feed from its **Calendar** page:

1. Sign in to Canvas in a web browser.
2. Open **Calendar** from global navigation.
3. Select **Calendar Feed** near the bottom of the sidebar.
4. Copy the iCal/ICS URL it shows you.
5. In Canvas Countdown, open **Settings → Canvas Calendar Feed**, paste the URL,
   and choose **Preview & Update…**.
6. Review the parsed events, deselect anything you do not want, then import.

The exact Canvas labels vary by institution.

**Treat this URL like a password.** It usually contains a private token that
grants read access to your calendar. Never paste it into an issue, a forum, or a
screenshot. The app stores it in your Keychain and redacts it from exported
diagnostics.

## Screenshot Import

**Add ▸ Import from Screenshot…** accepts PNG, JPEG, HEIC and TIFF by file
picker, drag and drop, or paste.

- Text recognition runs **locally on your Mac** using Apple Vision. No image and
  no recognised text leaves the machine, and no AI service is involved in this
  feature at all.
- **Every screenshot goes through a review screen.** Nothing detected is
  imported automatically. For each row you can correct the title, the course and
  the date and time, and see the text exactly as it was recognised.
- **Screenshots are held in memory only for the length of the review** and are
  dropped when the sheet closes. No image, image path or recognised text is
  written to the database or to exported diagnostics.

Recognition is imperfect, which is why the review step is not optional:

- A title or date may be read incorrectly and need correcting.
- If the year is not visible on screen, it is inferred and marked as inferred.
- Canvas layout varies by institution and theme, which reduces accuracy.
- A row with no visible "Due" line is kept for review rather than given an
  invented deadline.
- Availability lines such as "Not available until" are never read as deadlines.

**Check every date before you import it.**

## AI Assistant and Privacy

The Assistant is **enabled by default but local by default**: out of the box it
is pointed at `http://localhost:11434`, the address of a local model server such
as [Ollama](https://ollama.com). If nothing is running there, the Assistant
simply reports that it cannot be reached. Nothing is sent anywhere.

Two providers are offered in **Settings → AI Assistant**:

- **On this Mac** — a local server. Nothing is uploaded.
- **Groq (cloud)** — a hosted model. Choosing it is a separate, deliberate act
  and requires your own API key.

**What is sent when you use the cloud provider:** assignment titles, course
names and due dates for the deadlines currently in scope — and, when you ask
about one specific assignment, only that one. Nothing else is sent: not your
Canvas feed URL, not your completed or ignored marks, not the database, not any
screenshot or recognised text.

The privacy label in the Assistant's header reads the configured endpoint rather
than the provider's name, so pointing "local" at a remote host is reported as
cloud rather than as "On this Mac".

Your Groq API key is stored in the macOS Keychain, never in preferences or
diagnostics. Never publish your API key or your private schedule.

Anything the Assistant proposes is reviewed by you before it is saved. It never
edits or deletes an existing event, and an unreadable reply produces no task
rather than a task with an invented deadline.

## Privacy

- Assignment data, labels and settings stay on this Mac, in the app's sandbox
  container.
- The private Canvas feed URL is stored in the macOS Keychain — never in
  preferences, the database, or exported diagnostics.
- Feed downloads go directly from your Mac to the HTTPS URL you supplied.
- Screenshot recognition runs locally and screenshots are not retained after the
  review session.
- There is **no analytics, no tracking, no cloud sync and no application
  server**. The only outbound requests are the Canvas feed download and, if you
  switch the Assistant to a cloud provider, that provider's API.
- Notifications are scheduled locally by macOS.
- Exported diagnostics have URLs redacted before they are written.

## Known Limitations

- **The app is ad-hoc signed and not notarized.** First launch needs the
  right-click → Open step described under Installation.
- Automatic refresh runs only while the app is open; there is no background
  refresh yet.
- Canvas does not expose submission state in a calendar feed, so completed and
  ignored marks are maintained locally.
- Repeating calendar rules and detached recurrence instances are not expanded.
- The Dock countdown is live only while the app is running. macOS draws the
  static icon when the app is closed, and no app can change that.
- Screenshot OCR accuracy depends on your institution's Canvas theme.
- Launch at login depends on approval in **System Settings → General → Login
  Items**.
- Labels are not yet a filter: they colour and name an event, but Upcoming
  cannot be narrowed to one label.

## Feedback and Collaboration

Bugs are best raised as [GitHub issues](https://github.com/le23967/CanvasCountdown/issues).
For feedback or collaboration, email <operating333@gmail.com>.

## Reporting Issues

Use [GitHub Issues](https://github.com/le23967/CanvasCountdown/issues).

**Never include:**

- your Canvas feed URL or any part of its query string
- API keys or tokens of any kind
- screenshots containing your name, student ID, or other people's information
- real assignment titles or course details you would not post publicly

A description of what you did, what you expected and what happened is enough.
Exported diagnostics (**Settings → Export Diagnostics…**) already have URLs
redacted, but read the file before attaching it.

## Uninstallation and Data Removal

1. Quit Canvas Countdown.
2. Drag **Canvas Countdown** from **Applications** to the Bin.
3. Remove its data:

   ```sh
   rm -rf ~/Library/Containers/io.github.le23967.CanvasCountdown
   ```

   That single folder holds the database, preferences, labels and Dock themes.
4. Remove the Keychain entries: open **Keychain Access**, search for
   `com.local.CanvasCountdown`, and delete the `canvas-calendar-feed` and
   `assistant-api-key` items.
5. If you enabled launch at login, check **System Settings → General → Login
   Items**.

## Building from Source

```sh
xcodegen generate          # only needed if project.yml changed
xcodebuild -project CanvasCountdown.xcodeproj \
  -scheme CanvasCountdown \
  -destination 'platform=macOS' \
  test
```

`project.yml` is the XcodeGen source of truth; the checked-in `.xcodeproj` is
generated from it.

A release build and a DMG can be produced with:

```sh
scripts/build-release.sh
scripts/create-dmg.sh
```

Both write to `dist/`, which is not tracked by Git.

## How Refreshes Treat Your Data

- A Canvas event that disappears from one refresh is never deleted. It is
  counted as missing and archived only after three consecutive refreshes that
  carried a real event list and did not mention it.
- A failed download, a rejected redirect, an oversized response, a parser error,
  or a feed that parses but contains no events archives nothing.
- Archiving keeps the row, its deadline, and its completed and ignored state. If
  Canvas publishes the event again it returns with that state intact.
- An explicit `STATUS:CANCELLED` entry, or deselecting an event during import,
  archives it straight away.

## Tests

The suite covers calendar-day countdowns (including midnight and
daylight-saving boundaries), ICS UTC/all-day/TZID values, folded and escaped ICS
content, stable-UID merging, preservation of local completed/ignored state,
nearest-event selection, feed reconciliation and archiving, the download path
(chunking, size limits, redirects, cancellation), Dock tile layout at every Dock
size, screenshot parsing and review, the calendar's four scales and its date
entry, labels, and the selected-course scope.

The test bundle is hosted by the app, so the app process starts for every run.
An automated launch is detected and composed from isolated dependencies: an
in-memory store, an in-memory feed URL store, an offline fetcher, inert
notifications and an inert Dock renderer, with no refresh loops and no
login-item changes. A test run therefore cannot read the Keychain feed URL,
contact Canvas, or touch the production database. `LaunchIsolationTests` guards
each of those properties.

## Licence and Copyright

Copyright © 2026 le23967. Released under the MIT Licence — see
[LICENSE](LICENSE).

The MIT Licence lets anyone use, change and redistribute this code, including
in their own products, on one condition: the copyright notice above and the
licence text must be kept with any copy or substantial portion of it. Removing
the attribution, or passing the work off as someone else's, is the thing the
licence does not allow.
