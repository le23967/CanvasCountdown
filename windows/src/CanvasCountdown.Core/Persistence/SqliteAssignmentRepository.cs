using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CanvasCountdown;

/// <summary>
/// The events, in one SQLite table.
/// </summary>
/// <remarks>
/// <para>
/// Every change that touches more than one row runs in one transaction, so a
/// crash or a full disk leaves either all of it or none of it. Instants are
/// stored as UTC ticks: exact, sortable, and immune to the time zone the PC
/// happens to be in when the row is read back.
/// </para>
/// <para>
/// Operations run one at a time, like the actor the macOS repository is.
/// Each one reads the rows it needs, decides in memory exactly as the Swift
/// does, and writes back only what changed.
/// </para>
/// <para>
/// The merge rules are the macOS ones, and each exists because something went
/// wrong without it: a Canvas UID is the identity of an imported event; a
/// shared URL only upgrades a legacy row that never had a UID; completed,
/// ignored and the user's label are never overwritten by a refresh; and an
/// archived row returns with all of that intact when Canvas lists it again.
/// </para>
/// </remarks>
public sealed class SqliteAssignmentRepository : IAssignmentRepository, IDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim oneAtATime = new(1, 1);

    /// <param name="databasePath">A file path, or <c>:memory:</c> for a database that lives only as long as this object.</param>
    public SqliteAssignmentRepository(string databasePath)
    {
        if (databasePath != ":memory:")
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = databasePath == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        connection = new SqliteConnection(builder.ToString());
        connection.Open();
        Migrate();
    }

    public static SqliteAssignmentRepository InMemory() => new(":memory:");

    private void Migrate()
    {
        Execute("PRAGMA foreign_keys = ON;");
        if (connection.DataSource != ":memory:")
        {
            Execute("PRAGMA journal_mode = WAL;");
        }
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            // Written by a newer build. Reading it would risk misreading it,
            // and writing to it would risk destroying it.
            throw new InvalidOperationException("This database was created by a newer version of Canvas Countdown. Update the app to open it.");
        }
        if (version < 1)
        {
            using var transaction = connection.BeginTransaction();
            Execute(
                """
                CREATE TABLE IF NOT EXISTS assignments (
                    id TEXT PRIMARY KEY NOT NULL,
                    external_id TEXT,
                    title TEXT NOT NULL,
                    course_name TEXT,
                    due_date INTEGER NOT NULL,
                    source TEXT NOT NULL,
                    source_url TEXT,
                    is_completed INTEGER NOT NULL DEFAULT 0,
                    is_ignored INTEGER NOT NULL DEFAULT 0,
                    label_id TEXT,
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL,
                    last_seen_in_feed_at INTEGER,
                    missing_refresh_count INTEGER NOT NULL DEFAULT 0,
                    is_archived INTEGER NOT NULL DEFAULT 0,
                    archived_at INTEGER
                );
                CREATE INDEX IF NOT EXISTS assignments_due_date ON assignments (due_date);
                PRAGMA user_version = 1;
                """,
                transaction);
            transaction.Commit();
        }
    }

    // ------------------------------------------------------------------
    // Reading

    public Task<IReadOnlyList<AssignmentSnapshot>> FetchAllAsync() => FetchAllAsync(includingArchived: false);

    public async Task<IReadOnlyList<AssignmentSnapshot>> FetchAllAsync(bool includingArchived)
    {
        await oneAtATime.WaitAsync();
        try
        {
            return LoadRows()
                .Where(row => includingArchived || !row.IsArchived)
                .Select(row => row.Snapshot())
                .Order(Comparer<AssignmentSnapshot>.Create(DueDateOrder))
                .ToArray();
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    /// <summary>Earliest first; equal deadlines by title as a file browser sorts them.</summary>
    private static int DueDateOrder(AssignmentSnapshot left, AssignmentSnapshot right)
    {
        var byDate = left.DueDate.CompareTo(right.DueDate);
        return byDate != 0 ? byDate : TextOrder.Compare(left.Title, right.Title);
    }

    // ------------------------------------------------------------------
    // Importing

    public async Task<ImportResult> UpsertAsync(IReadOnlyList<AssignmentImportRecord> records, DateTimeOffset importedAt)
    {
        if (records.Count == 0)
        {
            return ImportResult.Empty;
        }
        await oneAtATime.WaitAsync();
        try
        {
            var stored = LoadRows();
            var externalIdIndex = new Dictionary<string, Row>(StringComparer.Ordinal);
            var sourceUrlIndex = new Dictionary<string, Row>(StringComparer.Ordinal);
            var fallbackIndex = new Dictionary<string, Row>(StringComparer.Ordinal);
            foreach (var row in stored)
            {
                if (ExternalIdKey(row.ExternalId, row.Source) is { } externalKey)
                {
                    externalIdIndex.TryAdd(externalKey, row);
                }
                if (SourceUrlKey(row.SourceUrl, row.Source) is { } urlKey)
                {
                    sourceUrlIndex.TryAdd(urlKey, row);
                }
                fallbackIndex[FallbackKey(row.Title, row.CourseName, row.DueDate, row.Source)] = row;
            }

            var inserted = 0;
            var updated = 0;
            var unchanged = 0;
            var created = new List<Row>();

            foreach (var raw in records)
            {
                var record = Sanitized(raw);
                var externalKey = ExternalIdKey(record.ExternalId, record.Source);
                var urlKey = SourceUrlKey(record.SourceUrl, record.Source);
                var fallbackKey = FallbackKey(record.Title, record.CourseName, record.DueDate, record.Source);

                Row? existing;
                if (externalKey is not null)
                {
                    // A URL may legitimately be shared by several Canvas
                    // VEVENTs. Only use it to upgrade a legacy row that did not
                    // yet have a stable UID.
                    existing = externalIdIndex.GetValueOrDefault(externalKey)
                        ?? (urlKey is not null && sourceUrlIndex.GetValueOrDefault(urlKey) is { ExternalId: null } legacy ? legacy : null);
                }
                else
                {
                    existing = (urlKey is not null ? sourceUrlIndex.GetValueOrDefault(urlKey) : null)
                        ?? fallbackIndex.GetValueOrDefault(fallbackKey);
                }

                if (existing is not null)
                {
                    if (record.Source == AssignmentSource.CanvasCalendarFeed)
                    {
                        // Presence bookkeeping is not a content change, so it
                        // does not make the row count as updated.
                        existing.LastSeenInFeedAt = importedAt;
                        existing.MissingRefreshCount = 0;
                        existing.IsArchived = false;
                        existing.ArchivedAt = null;
                        existing.IsDirty = true;
                    }
                    if (Apply(record, existing))
                    {
                        existing.UpdatedAt = importedAt;
                        updated++;
                    }
                    else
                    {
                        unchanged++;
                    }
                    if (externalKey is not null)
                    {
                        externalIdIndex[externalKey] = existing;
                    }
                    if (urlKey is not null)
                    {
                        sourceUrlIndex[urlKey] = existing;
                    }
                    fallbackIndex[FallbackKey(existing.Title, existing.CourseName, existing.DueDate, existing.Source)] = existing;
                    continue;
                }

                var row = new Row
                {
                    Id = Guid.NewGuid(),
                    ExternalId = record.ExternalId,
                    Title = record.Title,
                    CourseName = record.CourseName,
                    DueDate = record.DueDate,
                    Source = record.Source,
                    SourceUrl = record.SourceUrl,
                    CreatedAt = importedAt,
                    UpdatedAt = importedAt,
                    LastSeenInFeedAt = record.Source == AssignmentSource.CanvasCalendarFeed ? importedAt : null,
                    IsNew = true,
                };
                created.Add(row);
                inserted++;
                if (externalKey is not null)
                {
                    externalIdIndex[externalKey] = row;
                }
                if (urlKey is not null)
                {
                    sourceUrlIndex[urlKey] = row;
                }
                fallbackIndex[fallbackKey] = row;
            }

            Save(stored.Concat(created));
            return new ImportResult(inserted, updated, unchanged);
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    // ------------------------------------------------------------------
    // Manual events and the user's own marks

    public async Task<AssignmentSnapshot> SaveManualAsync(ManualAssignmentDraft draft, DateTimeOffset now)
    {
        var title = RequiredTitle(draft.Title);
        var courseName = OptionalText(draft.CourseName);
        var sourceUrl = OptionalText(draft.SourceUrl);

        await oneAtATime.WaitAsync();
        try
        {
            if (draft.Id is { } id)
            {
                var row = LoadRow(id) ?? throw new AssignmentRepositoryException(AssignmentRepositoryError.AssignmentNotFound);
                if (row.Source != AssignmentSource.Manual)
                {
                    throw new AssignmentRepositoryException(AssignmentRepositoryError.CannotEditImportedAssignmentAsManual);
                }
                row.Title = title;
                row.CourseName = courseName;
                row.DueDate = draft.DueDate;
                row.SourceUrl = sourceUrl;
                row.UpdatedAt = now;
                row.IsDirty = true;
                Save([row]);
                return row.Snapshot();
            }

            var created = new Row
            {
                Id = Guid.NewGuid(),
                Title = title,
                CourseName = courseName,
                DueDate = draft.DueDate,
                Source = AssignmentSource.Manual,
                SourceUrl = sourceUrl,
                CreatedAt = now,
                UpdatedAt = now,
                IsNew = true,
            };
            Save([created]);
            return created.Snapshot();
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    public async Task UpdateStatusAsync(Guid id, bool? isCompleted, bool? isIgnored, DateTimeOffset now)
    {
        await oneAtATime.WaitAsync();
        try
        {
            var row = LoadRow(id) ?? throw new AssignmentRepositoryException(AssignmentRepositoryError.AssignmentNotFound);
            var changed = false;
            if (isCompleted is { } completed && row.IsCompleted != completed)
            {
                row.IsCompleted = completed;
                changed = true;
            }
            if (isIgnored is { } ignored && row.IsIgnored != ignored)
            {
                row.IsIgnored = ignored;
                changed = true;
            }
            if (!changed)
            {
                return;
            }
            row.UpdatedAt = now;
            row.IsDirty = true;
            Save([row]);
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    public async Task SetLabelAsync(Guid id, Guid? labelId, DateTimeOffset now)
    {
        await oneAtATime.WaitAsync();
        try
        {
            var row = LoadRow(id) ?? throw new AssignmentRepositoryException(AssignmentRepositoryError.AssignmentNotFound);
            if (row.LabelId == labelId)
            {
                return;
            }
            row.LabelId = labelId;
            row.UpdatedAt = now;
            row.IsDirty = true;
            Save([row]);
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    public async Task<int> ClearLabelAsync(Guid labelId, DateTimeOffset now)
    {
        await oneAtATime.WaitAsync();
        try
        {
            var rows = LoadRows().Where(row => row.LabelId == labelId).ToArray();
            foreach (var row in rows)
            {
                row.LabelId = null;
                row.UpdatedAt = now;
                row.IsDirty = true;
            }
            Save(rows);
            return rows.Length;
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    // ------------------------------------------------------------------
    // What a refresh means for what is stored

    public async Task<FeedReconciliationOutcome> ReconcileCanvasFeedAsync(FeedReconciliationRequest request)
    {
        var active = request.ActiveExternalIds.Select(NormalizedExternalId).ToHashSet(StringComparer.Ordinal);
        var cancelled = request.CancelledExternalIds.Select(NormalizedExternalId).ToHashSet(StringComparer.Ordinal);
        var excluded = request.ExcludedExternalIds.Select(NormalizedExternalId).ToHashSet(StringComparer.Ordinal);
        var threshold = Math.Max(1, request.ArchiveThreshold);

        await oneAtATime.WaitAsync();
        try
        {
            var rows = LoadRows();
            int confirmed = 0, newlyMissing = 0, archived = 0, restored = 0;

            foreach (var row in rows.Where(row => row.Source == AssignmentSource.CanvasCalendarFeed))
            {
                if (row.ExternalId is not { } externalId)
                {
                    // A legacy row without a stable UID cannot be matched
                    // against the feed, so it is left exactly as it is.
                    continue;
                }
                var normalized = NormalizedExternalId(externalId);

                if (active.Contains(normalized))
                {
                    row.LastSeenInFeedAt = request.ObservedAt;
                    row.MissingRefreshCount = 0;
                    if (row.IsArchived)
                    {
                        // Canvas published it again: bring it back with its
                        // local completed and ignored state intact.
                        row.IsArchived = false;
                        row.ArchivedAt = null;
                        restored++;
                    }
                    row.IsDirty = true;
                    confirmed++;
                    continue;
                }

                if (excluded.Contains(normalized) || cancelled.Contains(normalized))
                {
                    // Explicit signals: the user deselected it during import,
                    // or Canvas published a cancellation for it.
                    if (!row.IsArchived)
                    {
                        Archive(row, request.ObservedAt);
                        archived++;
                    }
                    continue;
                }

                if (!request.CountsAbsentEventsAsMissing || row.IsArchived)
                {
                    continue;
                }

                row.MissingRefreshCount++;
                row.IsDirty = true;
                newlyMissing++;
                if (row.MissingRefreshCount >= threshold)
                {
                    Archive(row, request.ObservedAt);
                    archived++;
                }
            }

            Save(rows);
            return new FeedReconciliationOutcome
            {
                ConfirmedCount = confirmed,
                NewlyMissingCount = newlyMissing,
                ArchivedCount = archived,
                RestoredCount = restored,
            };
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    /// <summary>
    /// Archiving keeps the row, its deadline, and its completed and ignored
    /// state. Nothing about an absent Canvas event is ever destroyed here.
    /// </summary>
    private static void Archive(Row row, DateTimeOffset at)
    {
        row.IsArchived = true;
        row.ArchivedAt = at;
        row.IsDirty = true;
    }

    // ------------------------------------------------------------------
    // Deleting

    /// <summary>
    /// Deletes rather than archives: an archived row would still count against
    /// the course and would come back the moment Canvas published it again.
    /// Removing a course is a deletion, which is why it is confirmed first and
    /// why the course is blocked at the same time.
    /// </summary>
    public async Task<int> DeleteEventsInCourseAsync(string courseName)
    {
        if (CourseName.Normalized(courseName) is not { } key)
        {
            return 0;
        }
        await oneAtATime.WaitAsync();
        try
        {
            var doomed = LoadRows().Where(row => CourseName.Normalized(row.CourseName) == key).ToArray();
            Delete(doomed.Select(row => row.Id));
            return doomed.Length;
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    public async Task DeleteAsync(Guid id)
    {
        await oneAtATime.WaitAsync();
        try
        {
            if (LoadRow(id) is null)
            {
                throw new AssignmentRepositoryException(AssignmentRepositoryError.AssignmentNotFound);
            }
            Delete([id]);
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    public async Task DeleteAllAsync()
    {
        await oneAtATime.WaitAsync();
        try
        {
            Execute("DELETE FROM assignments;");
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    // ------------------------------------------------------------------
    // Rules

    private static bool Apply(AssignmentImportRecord record, Row row)
    {
        var changed = false;
        if (row.ExternalId != record.ExternalId)
        {
            row.ExternalId = record.ExternalId;
            changed = true;
        }
        if (row.Title != record.Title)
        {
            row.Title = record.Title;
            changed = true;
        }
        if (row.CourseName != record.CourseName)
        {
            row.CourseName = record.CourseName;
            changed = true;
        }
        if (row.DueDate != record.DueDate)
        {
            row.DueDate = record.DueDate;
            changed = true;
        }
        if (row.SourceUrl != record.SourceUrl)
        {
            row.SourceUrl = record.SourceUrl;
            changed = true;
        }
        if (changed)
        {
            row.IsDirty = true;
        }
        // Intentionally never overwrite IsCompleted, IsIgnored or LabelId.
        // Those are local decisions and must survive every Canvas refresh.
        return changed;
    }

    private static AssignmentImportRecord Sanitized(AssignmentImportRecord record) => record with
    {
        ExternalId = OptionalText(record.ExternalId),
        Title = RequiredTitle(record.Title),
        CourseName = OptionalText(record.CourseName),
        SourceUrl = OptionalText(record.SourceUrl),
    };

    private static string RequiredTitle(string value)
    {
        var trimmed = SwiftText.Trimmed(value);
        return trimmed.Length == 0 ? throw new AssignmentRepositoryException(AssignmentRepositoryError.EmptyTitle) : trimmed;
    }

    private static string? OptionalText(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var trimmed = SwiftText.Trimmed(value);
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string SourceName(AssignmentSource source) => source switch
    {
        AssignmentSource.CanvasCalendarFeed => "canvasCalendarFeed",
        AssignmentSource.Screenshot => "screenshot",
        _ => "manual",
    };

    private static string? ExternalIdKey(string? externalId, AssignmentSource source)
        => OptionalText(externalId) is { } value ? $"{SourceName(source)}|{SwiftText.Lowercased(value)}" : null;

    private static string NormalizedExternalId(string externalId) => SwiftText.Identity(externalId);

    private static string? SourceUrlKey(string? sourceUrl, AssignmentSource source)
        => OptionalText(sourceUrl) is { } value ? $"{SourceName(source)}|{value}" : null;

    /// <summary>
    /// The last resort for an event with neither a UID nor a link: what it is
    /// called, which course, and when, to the second.
    /// </summary>
    private static string FallbackKey(string title, string? courseName, DateTimeOffset dueDate, AssignmentSource source)
    {
        var normalizedTitle = SwiftText.Lowercased(SwiftText.Trimmed(title));
        var normalizedCourse = OptionalText(courseName) is { } course ? SwiftText.Lowercased(course) : string.Empty;
        var seconds = (long)Math.Round((dueDate - DateTimeOffset.UnixEpoch).TotalSeconds, MidpointRounding.AwayFromZero);
        return $"{SourceName(source)}|{normalizedTitle}|{normalizedCourse}|{seconds}";
    }

    // ------------------------------------------------------------------
    // Storage

    private sealed class Row
    {
        public Guid Id { get; init; }

        public string? ExternalId { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? CourseName { get; set; }

        public DateTimeOffset DueDate { get; set; }

        public AssignmentSource Source { get; init; }

        public string? SourceUrl { get; set; }

        public bool IsCompleted { get; set; }

        public bool IsIgnored { get; set; }

        public Guid? LabelId { get; set; }

        public DateTimeOffset CreatedAt { get; init; }

        public DateTimeOffset UpdatedAt { get; set; }

        public DateTimeOffset? LastSeenInFeedAt { get; set; }

        public int MissingRefreshCount { get; set; }

        public bool IsArchived { get; set; }

        public DateTimeOffset? ArchivedAt { get; set; }

        public bool IsNew { get; init; }

        public bool IsDirty { get; set; }

        public AssignmentSnapshot Snapshot() => new()
        {
            Id = Id,
            ExternalId = ExternalId,
            Title = Title,
            CourseName = CourseName,
            DueDate = DueDate,
            Source = Source,
            SourceUrl = SourceUrl,
            IsCompleted = IsCompleted,
            IsIgnored = IsIgnored,
            LabelId = LabelId,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            LastSeenInFeedAt = LastSeenInFeedAt,
            MissingRefreshCount = MissingRefreshCount,
            IsArchived = IsArchived,
            ArchivedAt = ArchivedAt,
        };
    }

    private const string Columns =
        "id, external_id, title, course_name, due_date, source, source_url, is_completed, is_ignored, label_id, created_at, updated_at, last_seen_in_feed_at, missing_refresh_count, is_archived, archived_at";

    private List<Row> LoadRows()
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM assignments;";
        return ReadRows(command);
    }

    private Row? LoadRow(Guid id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM assignments WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return ReadRows(command).FirstOrDefault();
    }

    private static List<Row> ReadRows(SqliteCommand command)
    {
        var rows = new List<Row>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new Row
            {
                Id = Guid.Parse(reader.GetString(0)),
                ExternalId = reader.IsDBNull(1) ? null : reader.GetString(1),
                Title = reader.GetString(2),
                CourseName = reader.IsDBNull(3) ? null : reader.GetString(3),
                DueDate = FromTicks(reader.GetInt64(4)),
                Source = reader.GetString(5) switch
                {
                    "canvasCalendarFeed" => AssignmentSource.CanvasCalendarFeed,
                    "screenshot" => AssignmentSource.Screenshot,
                    _ => AssignmentSource.Manual,
                },
                SourceUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
                IsCompleted = reader.GetInt64(7) != 0,
                IsIgnored = reader.GetInt64(8) != 0,
                LabelId = reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
                CreatedAt = FromTicks(reader.GetInt64(10)),
                UpdatedAt = FromTicks(reader.GetInt64(11)),
                LastSeenInFeedAt = reader.IsDBNull(12) ? null : FromTicks(reader.GetInt64(12)),
                MissingRefreshCount = (int)reader.GetInt64(13),
                IsArchived = reader.GetInt64(14) != 0,
                ArchivedAt = reader.IsDBNull(15) ? null : FromTicks(reader.GetInt64(15)),
            });
        }
        return rows;
    }

    /// <summary>Writes new and changed rows, all in one transaction.</summary>
    private void Save(IEnumerable<Row> rows)
    {
        var pending = rows.Where(row => row.IsNew || row.IsDirty).ToArray();
        if (pending.Length == 0)
        {
            return;
        }
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            INSERT INTO assignments ({Columns})
            VALUES ($id, $externalId, $title, $courseName, $dueDate, $source, $sourceUrl, $isCompleted, $isIgnored, $labelId, $createdAt, $updatedAt, $lastSeen, $missing, $isArchived, $archivedAt)
            ON CONFLICT(id) DO UPDATE SET
                external_id = excluded.external_id,
                title = excluded.title,
                course_name = excluded.course_name,
                due_date = excluded.due_date,
                source_url = excluded.source_url,
                is_completed = excluded.is_completed,
                is_ignored = excluded.is_ignored,
                label_id = excluded.label_id,
                updated_at = excluded.updated_at,
                last_seen_in_feed_at = excluded.last_seen_in_feed_at,
                missing_refresh_count = excluded.missing_refresh_count,
                is_archived = excluded.is_archived,
                archived_at = excluded.archived_at;
            """;
        foreach (var row in pending)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", row.Id.ToString("D"));
            command.Parameters.AddWithValue("$externalId", (object?)row.ExternalId ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", row.Title);
            command.Parameters.AddWithValue("$courseName", (object?)row.CourseName ?? DBNull.Value);
            command.Parameters.AddWithValue("$dueDate", row.DueDate.UtcTicks);
            command.Parameters.AddWithValue("$source", SourceName(row.Source));
            command.Parameters.AddWithValue("$sourceUrl", (object?)row.SourceUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$isCompleted", row.IsCompleted ? 1 : 0);
            command.Parameters.AddWithValue("$isIgnored", row.IsIgnored ? 1 : 0);
            command.Parameters.AddWithValue("$labelId", row.LabelId is { } label ? label.ToString("D") : DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", row.CreatedAt.UtcTicks);
            command.Parameters.AddWithValue("$updatedAt", row.UpdatedAt.UtcTicks);
            command.Parameters.AddWithValue("$lastSeen", row.LastSeenInFeedAt is { } seen ? seen.UtcTicks : DBNull.Value);
            command.Parameters.AddWithValue("$missing", row.MissingRefreshCount);
            command.Parameters.AddWithValue("$isArchived", row.IsArchived ? 1 : 0);
            command.Parameters.AddWithValue("$archivedAt", row.ArchivedAt is { } archived ? archived.UtcTicks : DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        foreach (var row in pending)
        {
            row.IsDirty = false;
        }
    }

    private void Delete(IEnumerable<Guid> ids)
    {
        var list = ids.ToArray();
        if (list.Length == 0)
        {
            return;
        }
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM assignments WHERE id = $id;";
        var parameter = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in list)
        {
            parameter.Value = id.ToString("D");
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static DateTimeOffset FromTicks(long ticks) => new(ticks, TimeSpan.Zero);

    private void Execute(string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        connection.Dispose();
        oneAtATime.Dispose();
    }
}
