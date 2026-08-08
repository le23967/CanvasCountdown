# Canvas Countdown for Windows — build brief

You are reading this on a Windows machine, in a checkout of the Canvas
Countdown repository. Your job is to build the Windows version of this app and
release it.

The macOS app is here, in Swift, finished and shipping. **It is the
specification.** This document tells you what was already decided, what you must
decide with the owner before you start, and the handful of rules that a rewrite
gets wrong every time. It does not restate the app's behaviour, because the
source is right next to you and will not go out of date.

---

## 0. Before you write any code

Three things to do first, in order.

1. **Check the toolchain.** Report what is actually installed — Visual Studio
   and its workloads, the .NET SDK version, the Windows App SDK, `git`, `gh`.
   Do not assume; say what is there and what is missing.
2. **Read the source.** At minimum:
   [`CanvasCountdown/Core/`](../CanvasCountdown/Core/) for the domain rules,
   [`CanvasCountdown/ViewModels/MainViewModel.swift`](../CanvasCountdown/ViewModels/MainViewModel.swift)
   for how the app hangs together, and [`README.md`](../README.md) for the
   fifteen features as the owner describes them publicly. The comments in this
   codebase explain *why*, not *what* — they are the most valuable thing in it,
   and most of them describe a mistake that was made once already.
3. **Settle the open decisions in §5 with the owner.** Two of them change what
   you build. Do not guess.

---

## 1. What this app is

A countdown to university assignment deadlines, for one student and the friends
they share it with. It imports a Canvas Calendar Feed, shows what is due and how
many days are left, reminds you before each one, and lets you add things by
hand, by screenshot, or by describing them in a sentence to a language model.

Two facts about its users shape every judgement call:

- **It is shared with friends.** A feature nobody can find is not shipped. A
  control nobody can decode is broken.
- **It holds real coursework.** Losing an event, or silently overwriting one, is
  the worst thing this app can do.

---

## 2. Decisions already made

| | |
|---|---|
| **Stack** | .NET 8 + WinUI 3 |
| **Location** | this repository, under `windows/` |
| **Tags** | `win-v1.0.0`, `win-v1.1.0`, … so they never compete with the macOS `v*` tags for `latest` |
| **Minimum OS** | Windows 10 20H1 (build 19041) or later, x64 and arm64 |

WinUI 3 was chosen because it makes every integration this app needs
first-party: tray icon, toast notifications, Credential Manager,
launch-at-login, and `Windows.Media.Ocr` for the screenshot import. Nothing here
needs a third-party wrapper.

Keep `windows/` self-contained. Nothing in it should be needed to build the
macOS app, and nothing in the macOS project should have to change for it.

---

## 3. Platform mapping

| macOS | Windows |
|---|---|
| SwiftUI, `NavigationSplitView` | WinUI 3, `NavigationView` |
| SwiftData | SQLite via `Microsoft.Data.Sqlite` |
| `UserDefaults` (`SettingsStore`) | your own settings store — see §4 for what is in it |
| Keychain: [`KeychainFeedURLStore.swift`](../CanvasCountdown/Services/KeychainFeedURLStore.swift), account `canvas-calendar-feed`; and the API key at [`AssistantService.swift`](../CanvasCountdown/Services/AssistantService.swift), account `assistant-api-key` | Credential Manager, or DPAPI |
| `UNUserNotificationCenter` | `AppNotificationManager` toasts |
| `NSDockTile` + [`DockTileRenderer.swift`](../CanvasCountdown/Core/DockTileRenderer.swift) | tray icon, redrawn — see §5 |
| `Vision` OCR | `Windows.Media.Ocr` |
| `ServiceManagement` (launch at login) | startup task, or the `Run` registry key |
| `NSWorkspace.open` | `Launcher.LaunchUriAsync` / `Process.Start` |
| SF Symbols | **Fluent System Icons or Lucide.** SF Symbols are licensed for Apple platforms only and must not be copied into this app. 23 distinct symbols are in use; find the nearest Fluent equivalent for each. |

---

## 4. The features

All fifteen from [`README.md`](../README.md). Ordered by what to build first —
each block is usable on its own, so the app is never in a state where it cannot
be run and looked at.

**Core (build first; without these there is no app)**

1. **Canvas Calendar Feed import** — paste a feed URL once, refresh on a
   schedule. Parser: [`ICSParser.swift`](../CanvasCountdown/Core/ICSParser.swift)
   (871 lines, and the tests beside it are worth as much as the code).
   Reconciliation: [`RefreshCoordinator.swift`](../CanvasCountdown/Services/RefreshCoordinator.swift).
   Read §6 before touching either.
2. **Upcoming / All Events / Completed lists** with the day countdown, and the
   next-deadline card.
3. **11:59 PM due times** — [`DueTimePolicy.swift`](../CanvasCountdown/Core/DueTimePolicy.swift).
   See §6; this one is subtle and it is switchable in Settings.
4. **Manual events** — add, edit, delete. Everything the assistant or an import
   produces is saved through this same path, so validation lives in one place.
5. **Course filtering** — one flat menu, plus the "show completed and ignored"
   toggle that lives at the top of it.
6. **Search** — a panel over the window, not a field in the toolbar, and it
   searches every assignment rather than the section on screen. It deliberately
   does not re-sort the list underneath it.

**Then**

7. **Notifications** — a schedule of rules, each "N days/hours before", capped
   at 10 rules. [`ReminderRule.swift`](../CanvasCountdown/Models/ReminderRule.swift),
   [`NotificationService.swift`](../CanvasCountdown/Services/NotificationService.swift).
   A rule of zero days is the due-day reminder.
8. **Calendar** — day, week, month and year views.
   [`AssignmentCalendar.swift`](../CanvasCountdown/Core/AssignmentCalendar.swift)
   and [`Views/Calendar/`](../CanvasCountdown/Views/Calendar/). On macOS these
   are ⌘1–⌘4, ⌘T for today, ⌘⇧T to type a date and jump to it. Use Ctrl on
   Windows and keep the rest.
9. **Labels** — a name and colour of the user's own, shown in the list and on
   the calendar. [`EventLabel.swift`](../CanvasCountdown/Models/EventLabel.swift).
10. **Course removal** — see §6; this is not the same as deleting events.
11. **Tells you when there is a new version** —
    [`ReleaseChecker.swift`](../CanvasCountdown/Services/ReleaseChecker.swift),
    [`AppRelease.swift`](../CanvasCountdown/Models/AppRelease.swift),
    [`UpdateDownloader.swift`](../CanvasCountdown/Services/UpdateDownloader.swift).
    Port the whole shape, pointed at the `win-v*` tags. On Windows you *can*
    install over yourself — the macOS build stops at opening the disk image only
    because it is sandboxed and unsigned. Decide this with the owner (§5).

**Then**

12. **AI deadline Assistant** — one composer that asks questions, drafts tasks
    from a sentence, or revises the drafts on screen; it reads which of the
    three you meant and says so before you send. Counts and dates are computed
    by the app and handed to the model, never asked of it.
    [`AssistantPanelView.swift`](../CanvasCountdown/Views/AssistantPanelView.swift),
    [`AssistantService.swift`](../CanvasCountdown/Services/AssistantService.swift),
    [`AssistantPresentation.swift`](../CanvasCountdown/Models/AssistantPresentation.swift).
13. **Any AI service, saved by name** — several saved models, each with its own
    key, switched from the composer.
    [`AssistantProfiles.swift`](../CanvasCountdown/Models/AssistantProfiles.swift).

**Last, and both need a decision first (§5)**

14. **Screenshot import with mandatory review** —
    [`ScreenshotImportCoordinator.swift`](../CanvasCountdown/Services/ScreenshotImportCoordinator.swift),
    [`CanvasScreenshotParser.swift`](../CanvasCountdown/Core/CanvasScreenshotParser.swift),
    [`CanvasDateTextParser.swift`](../CanvasCountdown/Core/CanvasDateTextParser.swift).
15. **Live countdown badge, custom appearance, reusable theme presets** —
    [`DockTileRenderer.swift`](../CanvasCountdown/Core/DockTileRenderer.swift),
    [`DockTileLayout.swift`](../CanvasCountdown/Core/DockTileLayout.swift),
    [`DockAppearance.swift`](../CanvasCountdown/Models/DockAppearance.swift),
    [`DockThemePresets.swift`](../CanvasCountdown/Models/DockThemePresets.swift).

---

## 5. Open decisions — ask the owner before building these

**5.1 What replaces the Dock countdown.** This is the app's signature: a live
day count painted onto the Dock icon, with a customisable appearance and saved
themes. It is three of the fifteen features and about 1,300 lines. Windows has
no Dock.

Recommended: **a tray icon redrawn with the day count**, keeping
`DockAppearance` and `DockThemePresets` intact so the colours, weights and
presets carry across unchanged. Say plainly when you propose it that Windows
hides tray icons in the overflow by default, so the owner will have to drag it
out to pin it — that is a real downgrade from a Dock tile and they should hear
it from you, not discover it.

The alternative is a taskbar overlay badge (16×16, two characters, only while
running) or a desktop widget. A badge cannot show "27 DAYS". Do not promise
otherwise.

**5.2 Whether the updater installs itself.** The macOS build downloads the disk
image and stops, because it is sandboxed and ad-hoc signed and cannot honestly
replace itself. A Windows build is under neither constraint, so a real
install-and-relaunch is possible. It is also the riskiest thing in the app —
it can leave someone with no working copy. Propose it, explain the risk, and let
the owner choose. If they say yes, it must be atomic and it must be tested by
actually updating a real installed copy.

**5.3 Screenshot import.** Recommended: `Windows.Media.Ocr` — first-party,
accurate, no bundle cost, and it never uploads anything. If it proves too weak
on Canvas screenshots, say so and offer to ship without this feature rather than
shipping one that reads dates wrong. Whatever recognises the text, **the review
step is not optional**: nothing recognised is ever saved without the user
checking it first.

---

## 6. The rules a rewrite gets wrong

Every one of these is a decision with a reason. None is obvious from outside.

**Midnight means the end of the day.**
[`DueTimePolicy.swift`](../CanvasCountdown/Core/DueTimePolicy.swift). Canvas
exports "due Friday" as 00:00 on Friday — the *first* instant of the day — for
work that is due at the end of it. The app shows 23:59 the same day instead. The
correction moves only the clock, never the date; it is applied when the list is
read and **never written to storage**, so switching it off in Settings puts the
original times straight back; and it never touches manual events, because a time
the user typed is not a Canvas artefact.

**The countdown counts calendar days, not elapsed time.**
[`CountdownCalculator.swift`](../CanvasCountdown/Core/CountdownCalculator.swift)
deliberately does not divide seconds by 86,400. That is wrong across daylight
saving and wrong near midnight. Compare start-of-day to start-of-day.

**A feed that lists nothing is not authoritative.**
[`RefreshCoordinator.swift`](../CanvasCountdown/Services/RefreshCoordinator.swift),
`carriesAuthoritativeEventList`. A response that parsed cleanly but described no
events must not be treated as "everything was deleted". Absence from such a
response counts against nothing. Events genuinely missing from real feeds are
archived only after a threshold of consecutive refreshes, never deleted on the
first miss.

**Removing a course blocks the name, not the events.**
[`CourseBlocklistStore.swift`](../CanvasCountdown/Services/CourseBlocklistStore.swift).
A Canvas feed carries every course the account ever enrolled in. Deleting those
events does not hold — the next refresh imports them again. Blocking the course
name makes the removal stick, and unblocking makes it reversible.

**The assistant is local-or-cloud, not a brand.**
[`AssistantSettings.swift`](../CanvasCountdown/Models/AssistantSettings.swift).
Every service worth using speaks the same chat-completions shape, so the
address, model and key are the whole configuration. Note the decoder: a stored
value it does not recognise must read as **local**. That is the only safe
direction to guess in — it cannot start uploading by accident. Keep that
property.

**The privacy label reads the endpoint, not the setting.** It says where the
request actually goes, so pointing a "local" configuration at a remote host
cannot produce a false claim of privacy.

**Version numbers compare as numbers.**
[`AppRelease.swift`](../CanvasCountdown/Models/AppRelease.swift). As text,
"1.10.0" sorts before "1.9.0". Get that backwards and everyone on 1.9.0 is told
they are current forever, and no later release can reach them to fix it. There
are tests for this in
[`UpdateCheckTests.swift`](../CanvasCountdownTests/UpdateCheckTests.swift).

---

## 7. Standards to carry over

**Nothing is written without a way back.** Batch saves say what they are about
to do and ask first; afterwards there is an undo that does not time out, because
a model asked for several tasks can get several of them wrong at once. See
[`UndoToastView.swift`](../CanvasCountdown/Views/Components/UndoToastView.swift)
and `assistantSaveSummary` in the view model. Imported data is never
overwritten.

**Labels, not bare glyphs.** An icon whose meaning has to be guessed is a bug.
The toolbar used to carry an eye that said "Hide" or "Show" — naming neither its
state nor what it acted on — and it was removed for exactly that reason. If a
control needs explaining, write the words.

**Findable, or it does not exist.** This app is shared with friends. A feature
behind an unmarked gesture may as well not have been built.

**Never use the owner's real coursework** in screenshots, documentation, test
fixtures, sample data or release notes. Invent subjects and assignment names.
The existing tests do this throughout — follow them.

---

## 8. Testing, and what "done" means

The macOS app has 549 tests over 14,185 lines, and they are the reason it can be
changed safely. Match that seriously: the domain rules in §6 in particular
should each have a test that fails if the rule is broken.

**The bar for reporting a feature done is that you launched the app and used
it.** Not that it compiles. Not that the tests pass. The macOS side of this port
could not check that, which is exactly why the work moved to your machine — so
do not waste the advantage. If you could not run something, say which part and
why.

---

## 9. Releasing

Mirror the macOS scripts: [`scripts/build-release.sh`](../scripts/build-release.sh)
and [`scripts/create-dmg.sh`](../scripts/create-dmg.sh). Note what they do
beyond building — they read the finished artefact back and refuse to package it
if it is wrong (architectures, bundle metadata, no test fixtures or logs inside,
signature valid). Write the Windows equivalent to be equally suspicious of its
own output.

- Tag `win-v1.0.0`. Attach the installer, and label the asset with the OS
  requirement and architectures.
- **Publish it as a pre-release until the owner has run it.** The website's
  download button follows `latest` and takes the first `.dmg`
  ([`docs/index.html`](../docs/index.html)); a Windows build promoted to
  `latest` would break that page for Mac users, and an untested build promoted
  to `latest` would reach the owner's friends before the owner. They can promote
  it themselves once it has been used.
- Verify after publishing by downloading the asset back and checking it matches
  byte for byte, as the macOS releases do.

---

## 10. Git rules

The owner has strict rules about repository history. They will give you the
exact wording; follow it literally. In summary:

- Commit only as the configured human author. Check `git config user.name` and
  `git config user.email` and show both before committing.
- **No co-authorship trailers, and no mention of any AI tool, assistant or
  vendor by name — and no "generated by" or "assisted by" phrasing — anywhere.**
  Not in commit messages, trailers, README files, changelogs, release notes,
  source comments or metadata. This document does not reproduce the owner's
  literal list of prohibited terms for one reason: the file would then contain
  the very strings the check searches for, and would fail it. Ask them for the
  list.
- Use the real current Australia/Sydney time.
- Make one focused commit. Show the exact proposed message before committing.
- After committing, run `git log -1 --format=fuller`.
- Then run the attribution scan they give you, over the whole history. It must
  produce no output. Run it over your new files too, not just commit messages.

---

## 11. If something here is wrong

This brief was written from the macOS side by someone who could not run
anything on Windows. If a recommendation turns out to be wrong on contact with
the real platform — the tray icon cannot be drawn the way §5.1 assumes, the OCR
is not good enough, the App SDK does not do what §3 claims — say so plainly and
propose the alternative. Do not implement something you can tell is a bad idea
because a document said to.
