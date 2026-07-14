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

