using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace CanvasCountdown.Tests.Assistant;

/// <summary>Shared fixtures. Every subject and course here is invented.</summary>
internal static class AssistantFixtures
{
    // Sydney moves its clocks forward at 02:00 on 4 October 2026 and back at
    // 03:00 on 5 April 2026.
    public static AppCalendar Sydney { get; } = AppCalendar.ForTimeZone("Australia/Sydney");

    public static CultureInfo EnglishUS { get; } = CultureInfo.GetCultureInfo("en-US");

    public static DateTimeOffset Date(int year, int month, int day, int hour = 9, int minute = 0)
        => Sydney.FromLocal(year, month, day, hour, minute);

    public static AssignmentListItem Item(
        string title,
        string? course,
        DateTimeOffset due,
        Guid? id = null,
        bool isCompleted = false,
        bool isIgnored = false)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            Title = title,
            CourseName = course,
            DueDate = due,
            IsCompleted = isCompleted,
            IsIgnored = isIgnored,
        };

    /// <summary>A completion of the common shape whose message says <paramref name="content"/>.</summary>
    public static string Completion(string content)
        => JsonSerializer.Serialize(new
        {
            id = "chatcmpl-invented",
            choices = new[] { new { index = 0, message = new { role = "assistant", content } } },
        });

    public static AssistantSettings EnabledLocal { get; } = AssistantSettings.Defaults with { IsEnabled = true };

    public static AssistantSettings EnabledCloud { get; } =
        AssistantSettings.Applying(AssistantProvider.Cloud, AssistantSettings.Defaults) with { IsEnabled = true };
}

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Authorization,
    string? ContentType,
    string? Body);

/// <summary>Stands in for the network: answers every request and records what was sent.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> respond;
    private readonly List<RecordedRequest> requests = [];

    public FakeHttpHandler(Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        this.respond = respond;
    }

    public FakeHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (requests)
            {
                return requests.ToList();
            }
        }
    }

    public RecordedRequest Single => Assert.Single(Requests);

    public static FakeHttpHandler Replying(HttpStatusCode status, string body)
        => new(_ => Response(status, body));

    public static FakeHttpHandler ReplyingWithCompletion(string content)
        => Replying(HttpStatusCode.OK, AssistantFixtures.Completion(content));

    /// <summary>Never answers; the request ends only when it is cancelled.</summary>
    public static FakeHttpHandler Hanging()
        => new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });

    public static HttpResponseMessage Response(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var authorization = request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : null;
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            authorization,
            request.Content?.Headers.ContentType?.ToString(),
            body);
        lock (requests)
        {
            requests.Add(recorded);
        }
        return await respond(recorded, cancellationToken);
    }
}

/// <summary>
/// Fails any request, so a test that accidentally reaches the network fails
/// loudly instead of going out to a real host, and counts how often it was asked.
/// </summary>
internal sealed class RefusingHandler : HttpMessageHandler
{
    private int calls;

    public int Calls => Volatile.Read(ref calls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        throw new HttpRequestException("No test may reach the network.");
    }
}

/// <summary>Reads out its text, then goes quiet until it is cancelled — a stalled download.</summary>
internal sealed class StallingStream : Stream
{
    private readonly byte[] data;
    private int position;

    public StallingStream(string text)
    {
        data = Encoding.UTF8.GetBytes(text);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (position < data.Length)
        {
            var count = Math.Min(buffer.Length, data.Length - position);
            data.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// An in-memory store that records every call made to it, so a test can say
/// exactly what was written — and that nothing already there was touched.
/// </summary>
internal sealed class RecordingRepository : IAssignmentRepository
{
    private readonly List<AssignmentSnapshot> events;
    private readonly List<string> calls = [];

    public RecordingRepository(params AssignmentSnapshot[] existing)
    {
        events = [.. existing];
    }

    public IReadOnlyList<string> Calls => calls;

    public List<ManualAssignmentDraft> SavedDrafts { get; } = [];

    public List<(Guid Id, Guid? LabelId)> LabelsSet { get; } = [];

    public IReadOnlyList<AssignmentSnapshot> Events => events;

    /// <summary>Set to make saving a particular draft fail.</summary>
    public Func<ManualAssignmentDraft, bool> FailsToSave { get; set; } = _ => false;

    /// <summary>Set to make putting a label on an event fail.</summary>
    public bool FailsToLabel { get; set; }

    public Task<IReadOnlyList<AssignmentSnapshot>> FetchAllAsync()
    {
        calls.Add(nameof(FetchAllAsync));
        return Task.FromResult<IReadOnlyList<AssignmentSnapshot>>(events.ToList());
    }

    public Task<IReadOnlyList<AssignmentSnapshot>> FetchAllAsync(bool includingArchived) => FetchAllAsync();

    public Task<ImportResult> UpsertAsync(IReadOnlyList<AssignmentImportRecord> records, DateTimeOffset importedAt)
    {
        calls.Add(nameof(UpsertAsync));
        return Task.FromResult(ImportResult.Empty);
    }

    public Task<AssignmentSnapshot> SaveManualAsync(ManualAssignmentDraft draft, DateTimeOffset now)
    {
        calls.Add(draft.Id is null ? "SaveManualAsync(new)" : "SaveManualAsync(edit)");
        SavedDrafts.Add(draft);
        if (FailsToSave(draft))
        {
            throw new AssignmentRepositoryException(AssignmentRepositoryError.EmptyTitle);
        }
        if (draft.Id is { } id)
        {
            var index = events.FindIndex(snapshot => snapshot.Id == id);
            if (index < 0)
            {
                throw new AssignmentRepositoryException(AssignmentRepositoryError.AssignmentNotFound);
            }
            events[index] = events[index] with { Title = draft.Title, CourseName = draft.CourseName, DueDate = draft.DueDate, UpdatedAt = now };
            return Task.FromResult(events[index]);
        }
        var created = new AssignmentSnapshot
        {
            Title = draft.Title,
            CourseName = string.IsNullOrWhiteSpace(draft.CourseName) ? null : draft.CourseName,
            DueDate = draft.DueDate,
            Source = AssignmentSource.Manual,
            CreatedAt = now,
            UpdatedAt = now,
        };
        events.Add(created);
        return Task.FromResult(created);
    }

    public Task UpdateStatusAsync(Guid id, bool? isCompleted, bool? isIgnored, DateTimeOffset now)
    {
        calls.Add(nameof(UpdateStatusAsync));
        return Task.CompletedTask;
    }

    public Task SetLabelAsync(Guid id, Guid? labelId, DateTimeOffset now)
    {
        calls.Add(nameof(SetLabelAsync));
        LabelsSet.Add((id, labelId));
        var index = events.FindIndex(snapshot => snapshot.Id == id);
        if (index < 0 || FailsToLabel)
        {
            throw new AssignmentRepositoryException(AssignmentRepositoryError.AssignmentNotFound);
        }
        events[index] = events[index] with { LabelId = labelId };
        return Task.CompletedTask;
    }

    public Task<int> ClearLabelAsync(Guid labelId, DateTimeOffset now)
    {
        calls.Add(nameof(ClearLabelAsync));
        return Task.FromResult(0);
    }

    public Task<FeedReconciliationOutcome> ReconcileCanvasFeedAsync(FeedReconciliationRequest request)
    {
        calls.Add(nameof(ReconcileCanvasFeedAsync));
        return Task.FromResult(FeedReconciliationOutcome.Empty);
    }

    public Task<int> DeleteEventsInCourseAsync(string courseName)
    {
        calls.Add(nameof(DeleteEventsInCourseAsync));
        return Task.FromResult(0);
    }

    public Task DeleteAsync(Guid id)
    {
        calls.Add(nameof(DeleteAsync));
        events.RemoveAll(snapshot => snapshot.Id == id);
        return Task.CompletedTask;
    }

    public Task DeleteAllAsync()
    {
        calls.Add(nameof(DeleteAllAsync));
        events.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>Answers for the local model server without one being there.</summary>
internal sealed class StubModelManager : ILocalModelManaging
{
    private readonly bool reachable;
    private readonly List<LocalModel> models;

    public StubModelManager(bool reachable, params LocalModel[] models)
    {
        this.reachable = reachable;
        this.models = [.. models];
    }

    public Task<bool> IsReachableAsync(Uri baseUrl, CancellationToken cancellationToken = default)
        => Task.FromResult(reachable);

    public Task<ValueList<LocalModel>> InstalledModelsAsync(Uri baseUrl, CancellationToken cancellationToken = default)
        => reachable
            ? Task.FromResult(models.ToValueList())
            : throw new LocalModelException(LocalModelError.ServerUnreachable);

    public Task PullAsync(string name, Uri baseUrl, IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        progress.Report(1);
        models.Add(new LocalModel { Name = name, SizeBytes = 1_000_000_000 });
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, Uri baseUrl, CancellationToken cancellationToken = default)
    {
        models.RemoveAll(model => model.Name == name);
        return Task.CompletedTask;
    }
}

/// <summary>Collects reported progress synchronously, unlike <see cref="Progress{T}"/>, which posts.</summary>
internal sealed class RecordingProgress : IProgress<double>
{
    private readonly List<double> values = [];

    public IReadOnlyList<double> Values
    {
        get
        {
            lock (values)
            {
                return values.ToList();
            }
        }
    }

    public void Report(double value)
    {
        lock (values)
        {
            values.Add(value);
        }
    }
}
