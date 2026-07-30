import Foundation

/// What Settings needs to manage courses, without reaching into the view model
/// or the store the blocklist lives in.
struct CourseManagementActions {
    /// Courses with events stored against them, most useful first: the name and
    /// how much removing it would take.
    var courses: [ManagedCourse]
    /// Courses already removed, so the removal can be undone.
    var blocked: [String]
    var remove: (String) -> Void
    var allow: (String) -> Void
}
