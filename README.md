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
state, and nearest-event selection.

Run tests in Xcode with **Product → Test**, or use the command above.

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

