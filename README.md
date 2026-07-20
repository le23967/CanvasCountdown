# Canvas Countdown

Canvas Countdown is a native macOS app that keeps Canvas LMS assignment
deadlines on your Mac and renders the nearest upcoming deadline as a large
calendar-day countdown in the Dock icon.

The app is built with Swift 6, SwiftUI, AppKit, SwiftData, Keychain Services,
URLSession, and UserNotifications. It has no analytics, account system,
external backend, or paid dependency.

## Requirements

- macOS 15 or later
- Xcode 16 or later (the project is generated and verified with Xcode 26.3)
- A Canvas Calendar Feed URL from your university's Canvas instance

## Open and run

1. Open `CanvasCountdown.xcodeproj` in Xcode.
2. Select the **CanvasCountdown** scheme and **My Mac** destination.
3. If Xcode requests it, choose your personal development team in
   **Signing & Capabilities**.
4. Press **Run**.

For command-line verification:

```sh
xcodebuild \
  -project CanvasCountdown.xcodeproj \
  -scheme CanvasCountdown \
  -destination 'platform=macOS' \
  test
```

`project.yml` is the XcodeGen source of truth. XcodeGen is only needed if the
project structure changes; it is not required to open or build the checked-in
Xcode project.

## Find the Canvas Calendar Feed URL

Canvas normally exposes the feed from its **Calendar** page:

1. Sign in to Canvas in a web browser.
2. Open **Calendar** from global navigation.
3. Select **Calendar Feed** near the bottom of the sidebar.
4. Copy the displayed iCal/ICS feed URL.
5. In Canvas Countdown, open **Settings → Canvas feed**, paste the URL, and
   choose **Preview Import**.
6. Review the parsed events, deselect anything you do not want, then import.

The exact Canvas labels can vary by institution. Treat this URL like a
password: it commonly contains a private token that grants read access to the
calendar.

## Import from a Canvas screenshot

The Canvas Calendar Feed is the recommended way to import deadlines: it stays
current by itself and refreshes automatically. Screenshot import is a secondary
method for when a feed is not available.

**File → Import from Canvas Screenshot…** accepts PNG, JPEG, HEIC and TIFF, by
file picker, drag and drop, or paste.

Text recognition runs locally on this Mac using Apple Vision. Screenshots are
never uploaded, and there is no cloud service, AI API or external backend
involved at any point.

Every screenshot goes through a review screen before anything is saved. Nothing
detected is imported automatically. For each row you can correct the title, the
course and the date and time, see the text exactly as it was recognised, and
choose whether to include it at all.

### What it does not do well

Recognition is imperfect, and the review step exists because of that:

- A title or date may be read incorrectly and need correcting.
- If the year is not visible on screen, it is inferred and marked as inferred.
- Canvas layout varies by institution and theme, which can reduce accuracy.
- A row with no visible "Due" line is kept for review rather than given an
  invented deadline.
- Availability lines such as "Not available until" are never treated as
  deadlines.

Screenshots are held in memory only for the length of the review and are
discarded when the sheet closes. No image, image path or recognised text is
written to the database or to exported diagnostics.

## The Dock icon

The application icon and the running Dock tile share one renderer, so they look
like the same object:

- **Not running:** the static icon shows the header and an em dash placeholder.
- **Running:** the same tile shows the live countdown to your nearest deadline.

The countdown is dynamic only while Canvas Countdown is running. macOS draws the
static icon from the app bundle when the app is closed, and no application can
change that image without running.

**Settings → Dock Appearance** adjusts the number size and weight, the
background, number and header colours, and offers four presets. Colours that
would be unreadable are corrected automatically.

## How refreshes treat your data

- A Canvas event that disappears from one refresh is never deleted. It is
  counted as missing and only archived after three consecutive refreshes that
  carried a real event list and did not mention it.
- A failed download, a rejected redirect, an oversized response, a parser
  error, or a feed that parses but contains no events archives nothing.
- Archiving keeps the row, its deadline, and its completed and ignored state.
  If Canvas publishes the event again it returns with that state intact.
- An explicit `STATUS:CANCELLED` entry, or deselecting an event during import,
  archives it straight away.

## Upcoming and Dock scope

**Settings → Upcoming and Dock Countdown** chooses between all assignments and
selected courses. The selection focuses the Upcoming list, its sidebar count,
the nearest-deadline card and the Dock countdown together. **All Events** and
**Completed** always show everything, and assignments without a course
(including manual entries) are always included. Nothing is deleted by this
setting.

## Privacy and security

- Assignment data and settings remain on this Mac.
- The private Canvas feed URL is stored in the user's Keychain, not
  `UserDefaults` or the SwiftData database.
- Feed downloads go directly from the Mac to the HTTPS URL supplied by the
  user.
- The app has no analytics, tracking, cloud sync, or application server.
- Logs and exported diagnostics never include the feed URL, its query string,
  or embedded credentials.
- Notifications are scheduled locally with macOS.
- Screenshot text recognition runs on this Mac. Screenshots are not uploaded,
  not stored after the review session, and never appear in diagnostics.

When reporting a bug on GitHub, do not attach your private Canvas feed URL or a
screenshot containing personal information.

## Tests

The test target covers calendar-day countdowns (including midnight and
daylight-saving boundaries), ICS UTC/all-day/TZID values, folded and escaped
ICS content, stable-UID merging, preservation of local completed/ignored
state, nearest-event selection, feed reconciliation and archiving, the download
path (chunking, size limits, redirects, cancellation), Dock tile layout at every
Dock size, and the selected-course scope.

Run tests in Xcode with **Product → Test**, or use the command above.

The test bundle is hosted by the app, so the app process starts for every run.
An automated launch is detected and composed from isolated dependencies: an
in-memory store, an in-memory feed URL store, an offline fetcher, inert
notifications and an inert Dock renderer, with no refresh or countdown loops and
no login-item changes. A test run therefore cannot read the Keychain feed URL,
contact Canvas, or touch the production database. `LaunchIsolationTests` guards
each of those properties.

## Known limitations

- Automatic refresh runs while the app is open. A future version can add a
  system background task without changing the feed/repository architecture.
- Canvas installations can customize calendar event text. The app uses the
  ICS event start as the deadline and deliberately does not infer due dates
  from prose such as “available from”.
- Canvas does not expose submission completion state in a calendar feed.
  Completed and ignored status is maintained locally.
- Repeating calendar rules and detached recurrence instances are not expanded
  in this version.
- Launch at login depends on macOS approval in **System Settings → General →
  Login Items**.

