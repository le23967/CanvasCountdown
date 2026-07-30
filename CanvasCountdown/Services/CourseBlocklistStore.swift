import Foundation

/// Courses the user removed, so a Canvas refresh cannot bring them back.
///
/// A Canvas Calendar Feed carries every course the account is enrolled in,
/// including ones finished long ago. Deleting those events alone would not
/// hold: the next refresh imports them again. Blocking the course name is what
/// makes the removal stick, and unblocking it makes the removal reversible.
///
/// Course names are not credentials and the private feed URL is never part of
/// this store. It lives beside the deselected-UID list rather than in
/// `SettingsStore` because the refresh coordinator reads it off the main actor.
protocol CourseBlocklisting: Sendable {
    /// The blocked courses as the user saw them written. Comparisons are made
    /// on the normalized form; this is the form to show back to them.
    func loadBlockedCourses() async -> Set<String>
    func block(_ courseName: String) async
    func unblock(_ courseName: String) async
    func clearBlockedCourses() async
}

extension CourseBlocklisting {
    /// True when a course, however the caller spelled it, is blocked.
    func isBlocked(_ courseName: String?) async -> Bool {
        guard let key = CourseName.normalized(courseName) else {
            return false
        }
        return await blockedCourseKeys().contains(key)
    }

    /// The set to match feed events against.
    func blockedCourseKeys() async -> Set<String> {
        Set(await loadBlockedCourses().compactMap(CourseName.normalized))
    }
}

/// One spelling rule, shared by everything that compares course names, so a
/// block written from Settings still matches what the parser produces.
///
/// Only ever used for comparison. What gets stored and shown is the name as it
/// was written, because "68101 physics lab" appearing in Settings under a
/// course the user knows as "68101 Physics Lab" reads like the app mangled it.
enum CourseName {
    static func normalized(_ courseName: String?) -> String? {
        guard let courseName else {
            return nil
        }
        let trimmed = courseName
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased(with: Locale(identifier: "en_US_POSIX"))
        return trimmed.isEmpty ? nil : trimmed
    }

    /// The name as written, trimmed. Nil when there is nothing left of it.
    static func display(_ courseName: String?) -> String? {
        guard let courseName else {
            return nil
        }
        let trimmed = courseName.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }
}

actor UserDefaultsCourseBlocklistStore: CourseBlocklisting {
    private let defaults: UserDefaults
    private let key: String

    init(
        defaults: UserDefaults = .standard,
        key: String = "CanvasCountdown.courses.blocked"
    ) {
        self.defaults = defaults
        self.key = key
    }

    func loadBlockedCourses() -> Set<String> {
        Set(
            (defaults.stringArray(forKey: key) ?? [])
                .compactMap(CourseName.display)
        )
    }

    func block(_ courseName: String) {
        guard let name = CourseName.display(courseName),
              let key = CourseName.normalized(courseName) else {
            return
        }
        var blocked = loadBlockedCourses()
        blocked = blocked.filter { CourseName.normalized($0) != key }
        blocked.insert(name)
        save(blocked)
    }

    func unblock(_ courseName: String) {
        guard let key = CourseName.normalized(courseName) else {
            return
        }
        save(
            loadBlockedCourses().filter {
                CourseName.normalized($0) != key
            }
        )
    }

    func clearBlockedCourses() {
        defaults.removeObject(forKey: self.key)
    }

    private func save(_ blocked: Set<String>) {
        defaults.set(blocked.sorted(), forKey: key)
    }
}

/// Keeps blocked courses in memory so an automated run cannot rewrite the
/// user's real list in `UserDefaults`.
actor IsolatedCourseBlocklistStore: CourseBlocklisting {
    private var blocked: Set<String> = []

    func loadBlockedCourses() -> Set<String> {
        blocked
    }

    func block(_ courseName: String) {
        guard let name = CourseName.display(courseName),
              let key = CourseName.normalized(courseName) else {
            return
        }
        blocked = blocked.filter { CourseName.normalized($0) != key }
        blocked.insert(name)
    }

    func unblock(_ courseName: String) {
        guard let key = CourseName.normalized(courseName) else {
            return
        }
        blocked = blocked.filter { CourseName.normalized($0) != key }
    }

    func clearBlockedCourses() {
        blocked.removeAll()
    }
}
