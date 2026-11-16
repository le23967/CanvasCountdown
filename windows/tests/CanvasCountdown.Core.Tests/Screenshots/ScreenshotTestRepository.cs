namespace CanvasCountdown.Tests.Screenshots;

/// <summary>
/// An in-memory store that behaves like the real repository where a screenshot
/// import can tell: records match on source and external identifier (ignoring
/// case and surrounding spaces), titles and optional text are tidied the same
/// way, an unchanged record counts as unchanged, and local completed, ignored
/// and label state is never touched by an import. Every write is counted, so a
/// test can prove that something wrote nothing.
/// </summary>
internal sealed class ScreenshotTestRepository : IAssignmentRepository
{
    private readonly object gate = new();
    private readonly List<AssignmentSnapshot> snapshots;

    public ScreenshotTestRepository(IEnumerable<AssignmentSnapshot>? snapshots = null)
    {
        this.snapshots = (snapshots ?? []).ToList();
    }

    /// <summary>How many calls have asked to change anything.</summary>
    public int WriteCount { get; private set; }

    /// <summary>When set, every read fails, as a damaged or locked store would.</summary>
    public bool FailsToRead { get; set; }

    public Task<IReadOnlyList<AssignmentSnapshot>> FetchAllAsync() => FetchAllAsync(includingArchived: false);

    public Task<IReadOnlyList<AssignmentSnapshot>> FetchAllAsync(bool includingArchived)
    {
        lock (gate)
        {
            if (FailsToRead)
            {
                throw new InvalidOperationException("The store could not be read.");
            }
            IReadOnlyList<AssignmentSnapshot> result = snapshots
                .Where(snapshot => includingArchived || !snapshot.IsArchived)
                .Order(Comparer<AssignmentSnapshot>.Create(AssignmentSnapshot.DueDateOrder))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<ImportResult> UpsertAsync(IReadOnlyList<AssignmentImportRecord> records, DateTimeOffset importedAt)
    {
        lock (gate)
        {
            WriteCount++;
            var inserted = 0;
            var updated = 0;
            var unchanged = 0;

            foreach (var raw in records)
            {
                var record = Sanitised(raw);
                var index = record.ExternalId is { } key
                    ? snapshots.FindIndex(stored => stored.Source == record.Source
                        && stored.ExternalId is { } storedKey
                        && string.Equals(storedKey.Trim(), key, StringComparison.OrdinalIgnoreCase))
                    : -1;

                if (index >= 0)
                {
                    var stored = snapshots[index];
                    var next = stored with
                    {
                        ExternalId = record.ExternalId,
                        Title = record.Title,
                        CourseName = record.CourseName,
                        DueDate = record.DueDate,
                        SourceUrl = record.SourceUrl,
                    };
                    if (next == stored)
                    {
                        unchanged++;
                        continue;
                    }
                    snapshots[index] = next with { UpdatedAt = importedAt };
                    updated++;
                    continue;
                }

                snapshots.Add(new AssignmentSnapshot
                {
                    ExternalId = record.ExternalId,
                    Title = record.Title,
                    CourseName = record.CourseName,
                    DueDate = record.DueDate,
                    Source = record.Source,
                    SourceUrl = record.SourceUrl,
                    CreatedAt = importedAt,
                    UpdatedAt = importedAt,
                    LastSeenInFeedAt = record.Source == AssignmentSource.CanvasCalendarFeed ? importedAt : null,
                });
                inserted++;
            }
            return Task.FromResult(new ImportResult(inserted, updated, unchanged));
        }
    }

    public Task<AssignmentSnapshot> SaveManualAsync(ManualAssignmentDraft draft, DateTimeOffset now)
    {
        lock (gate)
        {
            WriteCount++;
            var title = draft.Title.Trim();
            if (title.Length == 0)
            {
                throw new AssignmentRepositoryException(AssignmentRepositoryError.EmptyTitle);
            }
            if (draft.Id is { } id)
            {
                var index = IndexOf(id);
                if (snapshots[index].Source != AssignmentSource.Manual)
                {
                    throw new AssignmentRepositoryException(AssignmentRepositoryError.CannotEditImportedAssignmentAsManual);
                }
                snapshots[index] = snapshots[index] with
                {
                    Title = title,
                    CourseName = Optional(draft.CourseName),
                    DueDate = draft.DueDate,
                    SourceUrl = Optional(draft.SourceUrl),
                    UpdatedAt = now,
                };
                return Task.FromResult(snapshots[index]);
            }

            var saved = new AssignmentSnapshot
            {
                Title = title,
                CourseName = Optional(draft.CourseName),
                DueDate = draft.DueDate,
                Source = AssignmentSource.Manual,
                SourceUrl = Optional(draft.SourceUrl),
                CreatedAt = now,
                UpdatedAt = now,
            };
            snapshots.Add(saved);
            return Task.FromResult(saved);
        }
    }

    public Task UpdateStatusAsync(Guid id, bool? isCompleted, bool? isIgnored, DateTimeOffset now)
    {
        lock (gate)
        {
            WriteCount++;
            var index = IndexOf(id);
            snapshots[index] = snapshots[index] with
            {
                IsCompleted = isCompleted ?? snapshots[index].IsCompleted,
                IsIgnored = isIgnored ?? snapshots[index].IsIgnored,
                UpdatedAt = now,
            };
            return Task.CompletedTask;
        }
    }

    public Task SetLabelAsync(Guid id, Guid? labelId, DateTimeOffset now)
    {
        lock (gate)
        {
            WriteCount++;
            var index = IndexOf(id);
            snapshots[index] = snapshots[index] with { LabelId = labelId, UpdatedAt = now };
            return Task.CompletedTask;
        }
    }

    public Task<int> ClearLabelAsync(Guid labelId, DateTimeOffset now)
    {
        lock (gate)
        {
            WriteCount++;
            var cleared = 0;
            for (var index = 0; index < snapshots.Count; index++)
            {
                if (snapshots[index].LabelId == labelId)
                {
                    snapshots[index] = snapshots[index] with { LabelId = null, UpdatedAt = now };
                    cleared++;
                }
            }
            return Task.FromResult(cleared);
        }
    }

    public Task DeleteAsync(Guid id)
    {
        lock (gate)
        {
            WriteCount++;
            snapshots.RemoveAt(IndexOf(id));
            return Task.CompletedTask;
        }
    }

    public Task DeleteAllAsync()
    {
        lock (gate)
        {
            WriteCount++;
            snapshots.Clear();
            return Task.CompletedTask;
        }
    }

    private int IndexOf(Guid id)
    {
        var index = snapshots.FindIndex(snapshot => snapshot.Id == id);
        return index >= 0 ? index : throw new AssignmentRepositoryException(AssignmentRepositoryError.AssignmentNotFound);
    }

    private static AssignmentImportRecord Sanitised(AssignmentImportRecord record)
    {
        var title = record.Title.Trim();
        if (title.Length == 0)
        {
            throw new AssignmentRepositoryException(AssignmentRepositoryError.EmptyTitle);
        }
        return record with
        {
            ExternalId = Optional(record.ExternalId),
            Title = title,
            CourseName = Optional(record.CourseName),
            SourceUrl = Optional(record.SourceUrl),
        };
    }

    private static string? Optional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
