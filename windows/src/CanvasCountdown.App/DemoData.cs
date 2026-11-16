namespace CanvasCountdown.App;

/// <summary>
/// Invented coursework for isolated launches: the UI tests and any screenshot
/// meant for the website or a README.
/// </summary>
/// <remarks>
/// Every subject, title and date here is made up. Nothing public may ever
/// show someone's real courses — a subject code and a timetable identify a
/// person at an institution — so pictures of the app are taken from this, in
/// an isolated launch that cannot see the real database.
/// </remarks>
internal static class DemoData
{
    public static async Task SeedAsync(IAssignmentRepository repository, SettingsStore settings)
    {
        var calendar = AppCalendar.Current;
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset At(int days, int hour, int minute) => calendar.SettingTime(calendar.AddDays(now, days), hour, minute);

        // Canvas-shaped records: some at midnight, which the due-time policy
        // shows as 11:59 PM, as Canvas itself does.
        await repository.UpsertAsync(
            [
                Imported("demo-1", "Problem Set 4", "COMP2050", At(2, 0, 0)),
                Imported("demo-2", "Lab Report: Regression", "STAT1010", At(5, 17, 0)),
                Imported("demo-3", "Studio Critique Reflection", "DESN1500", At(9, 0, 0)),
                Imported("demo-4", "Quiz 3", "STAT1010", At(1, 10, 30)),
                Imported("demo-5", "Group Project Proposal", "COMP2050", At(16, 23, 59)),
                Imported("demo-6", "Reading Response 2", "DESN1500", At(-3, 0, 0)),
            ],
            now);

        var manual = await repository.SaveManualAsync(
            new ManualAssignmentDraft { Title = "Book library study room", DueDate = At(3, 12, 0) },
            now);

        var events = await repository.FetchAllAsync();
        var important = settings.EventLabels.Labels.FirstOrDefault();
        foreach (var snapshot in events)
        {
            if (snapshot.ExternalId == "demo-6")
            {
                await repository.UpdateStatusAsync(snapshot.Id, isCompleted: true, isIgnored: null, now);
            }
            if (snapshot.ExternalId == "demo-1" && important is not null)
            {
                await repository.SetLabelAsync(snapshot.Id, important.Id, now);
            }
        }
        if (settings.EventLabels.Labels.Skip(1).FirstOrDefault() is { } personal)
        {
            await repository.SetLabelAsync(manual.Id, personal.Id, now);
        }
    }

    private static AssignmentImportRecord Imported(string id, string title, string course, DateTimeOffset due) => new()
    {
        ExternalId = id,
        Title = title,
        CourseName = course,
        DueDate = due,
        Source = AssignmentSource.CanvasCalendarFeed,
    };
}
