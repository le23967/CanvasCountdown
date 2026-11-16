using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CanvasCountdown;

/// <summary>
/// A task the assistant proposed. Never written to storage directly: it goes
/// through review, and then through the same manual-event path everything
/// else uses (<see cref="AssistantDraftReview.SaveAsync"/>).
/// </summary>
public sealed record AssistantDraftTask
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public bool Include { get; init; } = true;

    public required string Title { get; init; }

    /// <summary>
    /// The course, if the sentence named one. Editable during review, because
    /// the model guesses this from a sentence and the user knows.
    /// </summary>
    public string? CourseName { get; init; }

    /// <summary>
    /// The user's own label. Never guessed — the model is not given the label
    /// list and cannot set this — but choosing one here saves finding the event
    /// in the list afterwards just to mark it.
    /// </summary>
    public Guid? LabelId { get; init; }

    public required DateTimeOffset? DueDate { get; init; }

    public required string SourceText { get; init; }

    public bool CanBeSaved => DueDate is not null && Title.Trim().Length > 0;

    /// <summary>
    /// Whether saving the review writes this one: ticked, named and dated. The
    /// confirmation counts by this same rule, so it says what the save does.
    /// </summary>
    public bool WillBeSaved => Include && CanBeSaved;

    /// <summary>
    /// The course as typed in review. A field left empty, or holding only
    /// spaces, means no course; anything else is kept as typed.
    /// </summary>
    public AssistantDraftTask WithCourseText(string text)
        => this with { CourseName = text.Trim().Length == 0 ? null : text };

    /// <summary>
    /// The model gave no date, and the app will not invent one; when the user
    /// asks to set one, the picker starts at this time tomorrow for them to
    /// change.
    /// </summary>
    public AssistantDraftTask WithPlaceholderDate(DateTimeOffset now, AppCalendar calendar)
        => this with { DueDate = calendar.AddDays(now, 1) };

    /// <summary>
    /// The new manual event this draft becomes: title and course trimmed, and
    /// never an id, so saving a draft can only ever add an event. Null when
    /// there is no date to save.
    /// </summary>
    public ManualAssignmentDraft? ToManualDraft()
        => DueDate is { } dueDate
            ? new ManualAssignmentDraft
            {
                Title = Title.Trim(),
                CourseName = CourseName?.Trim(),
                DueDate = dueDate,
            }
            : null;
}

public enum AssistantMessageRole
{
    User,
    Assistant,
}

public static class AssistantMessageRoleExtensions
{
    /// <summary>Who said it, as the conversation names them.</summary>
    public static string Title(this AssistantMessageRole role) => role switch
    {
        AssistantMessageRole.User => "You",
        AssistantMessageRole.Assistant => "Assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}

/// <summary>
/// One turn of the conversation.
/// </summary>
/// <remarks>
/// Stored and dated because the conversation is kept between launches: without
/// a record, there is no way to tell whether something has already been asked,
/// and no way to read the answer again once the window has closed.
/// </remarks>
public sealed record AssistantMessage
{
    [JsonRequired]
    public Guid Id { get; init; } = Guid.NewGuid();

    public required AssistantMessageRole Role { get; init; }

    public required string Text { get; init; }

    /// <summary>
    /// When it was said, so a conversation reopened next week still says when
    /// it happened.
    /// </summary>
    public required DateTimeOffset Date { get; init; }

    /// <summary>The time beside the speaker's name, on <paramref name="calendar"/>'s clock.</summary>
    public string TimeText(AppCalendar calendar, CultureInfo culture)
        => calendar.ToLocal(Date).ToString("t", culture);
}

/// <summary>
/// Facts the app has worked out itself.
/// </summary>
/// <remarks>
/// Counting and date arithmetic are given to the model rather than asked of it.
/// A small local model will happily miscount a list; the app cannot. The model
/// is left to do what it is actually good at, which is judgement and phrasing.
/// Days are calendar days in <see cref="AppCalendar"/>'s zone, never elapsed
/// hours.
/// </remarks>
public sealed record AssistantFacts
{
    public AssistantFacts(IReadOnlyList<AssistantAssignmentDigest> digests, DateTimeOffset now, AppCalendar calendar)
    {
        int Days(DateTimeOffset date) => calendar.DaysBetween(now, date);

        Total = digests.Count;
        DueThisWeek = digests.Count(digest => Days(digest.DueDate) is >= 0 and <= 7);
        DueThisMonth = digests.Count(digest => Days(digest.DueDate) is >= 0 and <= 31);
        Overdue = digests.Count(digest => Days(digest.DueDate) < 0);

        AssistantAssignmentDigest? nearest = null;
        foreach (var digest in digests)
        {
            if (nearest is null || digest.DueDate < nearest.DueDate)
            {
                nearest = digest;
            }
        }
        NearestTitle = nearest?.Title;
        NearestInDays = nearest is null ? null : Days(nearest.DueDate);
    }

    public int Total { get; }

    public int DueThisWeek { get; }

    public int DueThisMonth { get; }

    public int Overdue { get; }

    public string? NearestTitle { get; }

    public int? NearestInDays { get; }

    public string PromptLines
    {
        get
        {
            var lines = new List<string>
            {
                string.Create(CultureInfo.InvariantCulture, $"Total upcoming: {Total}"),
                string.Create(CultureInfo.InvariantCulture, $"Due within 7 days: {DueThisWeek}"),
                string.Create(CultureInfo.InvariantCulture, $"Due within 31 days: {DueThisMonth}"),
            };
            if (Overdue > 0)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"Already past their date: {Overdue}"));
            }
            if (NearestTitle is not null && NearestInDays is { } days)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"Nearest: {NearestTitle}, in {days} days"));
            }
            return string.Join("\n", lines);
        }
    }
}

/// <summary>
/// The assistant, as the rest of the app sees it. Nothing here can change an
/// event: answers are text, and drafts are values for the user to review.
/// </summary>
public interface IAssistantServicing
{
    /// <summary>Answers a question about the workload. Read-only: it changes nothing.</summary>
    Task<string> AnswerAsync(
        string question,
        IReadOnlyList<AssistantMessage> history,
        IReadOnlyList<AssistantAssignmentDigest> digests,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>A short plain-language summary of the workload. Read-only.</summary>
    Task<string> SummariseAsync(
        IReadOnlyList<AssistantAssignmentDigest> digests,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>Turns a sentence into draft tasks for review. Never saves anything.</summary>
    Task<ValueList<AssistantDraftTask>> DraftTasksAsync(
        string text,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes drafts that already exist, rather than starting again.
    /// </summary>
    /// <remarks>
    /// Without this, "put those under COMP2050" means retyping the sentence that
    /// produced them, and the reply is a fresh guess that loses whatever was
    /// already corrected by hand. The current drafts go with the request, so
    /// the model is editing a list rather than inventing one.
    /// </remarks>
    Task<ValueList<AssistantDraftTask>> ReviseDraftsAsync(
        IReadOnlyList<AssistantDraftTask> drafts,
        string instruction,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Talks to any chat completions endpoint of the common shape, which covers
/// both a local model server and the hosted services.
/// </summary>
/// <remarks>
/// Holds nothing that changes, so one instance can serve concurrent requests.
/// Every failure surfaces as an <see cref="AssistantException"/>.
/// </remarks>
public sealed class ChatCompletionsAssistantService : IAssistantServicing
{
    /// <summary>How long a request may wait for an answer before it counts as unreachable.</summary>
    public static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(30);

    private const string DayFormat = "yyyy-MM-dd";
    private const string DueFormat = "yyyy-MM-dd'T'HH:mm";

    private readonly AssistantSettings settings;
    private readonly string? apiKey;
    private readonly AppCalendar calendar;
    private readonly HttpClient client;
    private readonly TimeSpan requestTimeout;

    /// <param name="handler">
    /// The transport, for tests. Without one, requests go through a shared
    /// client that keeps no cookies.
    /// </param>
    public ChatCompletionsAssistantService(
        AssistantSettings settings,
        string? apiKey,
        AppCalendar calendar,
        HttpMessageHandler? handler = null)
        : this(settings, apiKey, calendar, handler, RequestTimeout)
    {
    }

    internal ChatCompletionsAssistantService(
        AssistantSettings settings,
        string? apiKey,
        AppCalendar calendar,
        HttpMessageHandler? handler,
        TimeSpan requestTimeout)
    {
        this.settings = settings;
        this.apiKey = apiKey;
        this.calendar = calendar;
        this.requestTimeout = requestTimeout;
        client = AssistantHttp.ClientFor(handler);
    }

    public async Task<string> AnswerAsync(
        string question,
        IReadOnlyList<AssistantMessage> history,
        IReadOnlyList<AssistantAssignmentDigest> digests,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var trimmed = question.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var facts = new AssistantFacts(digests, now, calendar);
        var lines = string.Join(
            "\n",
            digests.OrderBy(digest => digest.DueDate).Take(40).Select(digest => digest.Line(now, calendar)));

        // Recent turns only: enough to follow up, not enough to send the whole
        // session every time.
        var recent = history
            .Skip(Math.Max(0, history.Count - 6))
            .Select(turn => (Role: turn.Role == AssistantMessageRole.User ? "user" : "assistant", Content: turn.Text))
            .ToList();

        var system =
            "You help a student manage coursework deadlines. Answer briefly and "
            + "concretely, in the user's language. Use only the figures and items "
            + "given below; the counts are already correct, so do not recount "
            + "them. Never invent a deadline that is not listed. If something is "
            + "not in the list, say so.\n"
            + "\n"
            + "Figures:\n"
            + facts.PromptLines + "\n"
            + "\n"
            + "Items:\n"
            + (lines.Length == 0 ? "none" : lines);

        return await SendAsync(system, trimmed, recent, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> SummariseAsync(
        IReadOnlyList<AssistantAssignmentDigest> digests,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (digests.Count == 0)
        {
            return "Nothing is coming up.";
        }
        // Only the three agreed fields are ever composed into a prompt.
        var lines = string.Join(
            "\n",
            digests.OrderBy(digest => digest.DueDate).Select(digest => digest.Line(now, calendar)));

        var system =
            "You help a student plan coursework. Be brief and concrete. "
            + "Never invent deadlines that are not listed.";
        var user =
            "Here is what is due:\n"
            + lines + "\n"
            + "\n"
            + "In at most four sentences, say what to start first and why.";

        return await SendAsync(system, user, [], cancellationToken).ConfigureAwait(false);
    }

    public async Task<ValueList<AssistantDraftTask>> DraftTasksAsync(
        string text,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        var today = calendar.DateOf(now).ToString(DayFormat, CultureInfo.InvariantCulture);
        var system =
            "Extract tasks with deadlines. Reply with JSON only, as "
            + "{\"tasks\":[{\"title\":\"...\",\"course\":null,\"due\":\"YYYY-MM-DDTHH:MM\"}]}. "
            + "Today is " + today + ". Use 23:59 when no time is given. "
            + "If no deadline is stated, use null for due.";

        var reply = await SendAsync(system, trimmed, [], cancellationToken).ConfigureAwait(false);
        return ParseDrafts(reply, trimmed, calendar);
    }

    public async Task<ValueList<AssistantDraftTask>> ReviseDraftsAsync(
        IReadOnlyList<AssistantDraftTask> drafts,
        string instruction,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var trimmed = instruction.Trim();
        if (trimmed.Length == 0 || drafts.Count == 0)
        {
            return drafts.ToValueList();
        }

        var today = calendar.DateOf(now).ToString(DayFormat, CultureInfo.InvariantCulture);
        var system =
            "You are editing a list of draft tasks that already exists. Here it "
            + "is as JSON:\n"
            + "\n"
            + DraftsJson(drafts, calendar) + "\n"
            + "\n"
            + "Apply the change the user asks for and reply with JSON only, in "
            + "that same shape: "
            + "{\"tasks\":[{\"id\":\"...\",\"title\":\"...\",\"course\":null,\"due\":\"YYYY-MM-DDTHH:MM\"}]}. "
            + "Return the whole list, not only what changed. Keep the id of every "
            + "task you are keeping, so edits line up with what is on screen; use "
            + "a new id only for a task you are adding, and leave a task out only "
            + "if the user asked for it to go. Today is " + today + ". "
            + "Use 23:59 when no time is given.";

        var reply = await SendAsync(system, trimmed, [], cancellationToken).ConfigureAwait(false);
        return ParseRevisedDrafts(reply, drafts, calendar);
    }

    /// <summary>
    /// The drafts as the model will be shown them. Only the three fields it is
    /// allowed to change are included, with the id that lines them up; the
    /// label and the tick are the user's and are never sent or asked about.
    /// </summary>
    public static string DraftsJson(IReadOnlyList<AssistantDraftTask> drafts, AppCalendar calendar)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, AssistantJson.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("tasks");
            foreach (var draft in drafts)
            {
                writer.WriteStartObject();
                writer.WriteString("id", AssistantText.UuidString(draft.Id));
                writer.WriteString("title", draft.Title);
                if (draft.CourseName is { } course)
                {
                    writer.WriteString("course", course);
                }
                else
                {
                    writer.WriteNull("course");
                }
                if (draft.DueDate is { } due)
                {
                    writer.WriteString("due", calendar.ToLocal(due).ToString(DueFormat, CultureInfo.InvariantCulture));
                }
                else
                {
                    writer.WriteNull("due");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Reads a revision without letting it undo the user's own work.
    /// </summary>
    /// <remarks>
    /// A task the model kept keeps the tick and the label that were put on it
    /// here, because the model was never told about either and cannot have
    /// meant to change them. A reply that cannot be read at all returns the
    /// drafts untouched rather than emptying the review.
    /// </remarks>
    public static ValueList<AssistantDraftTask> ParseRevisedDrafts(
        string reply,
        IReadOnlyList<AssistantDraftTask> drafts,
        AppCalendar calendar)
    {
        var sourceText = drafts.Count == 0 ? string.Empty : drafts[0].SourceText;
        var parsed = ParseDrafts(reply, sourceText, calendar, preservingIds: true);
        if (parsed.IsEmpty)
        {
            return drafts.ToValueList();
        }
        var existing = new Dictionary<Guid, AssistantDraftTask>();
        foreach (var draft in drafts)
        {
            existing.TryAdd(draft.Id, draft);
        }
        return parsed
            .Select(task => existing.TryGetValue(task.Id, out var previous)
                ? task with { Include = previous.Include, LabelId = previous.LabelId }
                : task)
            .ToValueList();
    }

    /// <summary>
    /// Reads the model's JSON without trusting it. A malformed reply yields no
    /// drafts rather than a wrong deadline, and a task with no date — or a date
    /// that is not a real one — arrives with <see cref="AssistantDraftTask.DueDate"/>
    /// null, so review must supply one.
    /// </summary>
    /// <param name="preservingIds">
    /// Whether an <c>id</c> in the reply is honoured. Only a revision needs
    /// this: a first draft has nothing to line up with, and trusting an
    /// invented id there could collide with a task the user is already looking
    /// at. An id that is not a well-formed UUID becomes a new draft.
    /// </param>
    public static ValueList<AssistantDraftTask> ParseDrafts(
        string reply,
        string sourceText,
        AppCalendar calendar,
        bool preservingIds = false)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end < 0 || start >= end)
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(reply[start..(end + 1)], AssistantJson.DocumentOptions);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !AssistantJson.TryGetFirst(document.RootElement, "tasks", out var tasks)
                || !AssistantJson.IsArrayOfObjects(tasks))
            {
                return [];
            }

            var drafts = new List<AssistantDraftTask>();
            foreach (var entry in tasks.EnumerateArray())
            {
                var title = AssistantJson.StringValue(entry, "title")?.Trim();
                if (string.IsNullOrEmpty(title))
                {
                    continue;
                }
                var course = AssistantJson.StringValue(entry, "course")?.Trim();
                var due = AssistantJson.StringValue(entry, "due") is { } dueText ? ParseDue(dueText, calendar) : null;
                var id = preservingIds && AssistantJson.StringValue(entry, "id") is { } idText && AssistantText.ParseUuid(idText) is { } kept
                    ? kept
                    : Guid.NewGuid();
                drafts.Add(new AssistantDraftTask
                {
                    Id = id,
                    Title = title,
                    CourseName = string.IsNullOrEmpty(course) ? null : course,
                    DueDate = due,
                    SourceText = sourceText,
                });
            }
            return drafts.ToValueList();
        }
    }

    /// <summary>
    /// A due date written <c>YYYY-MM-DDTHH:MM</c>, read on <paramref name="calendar"/>'s clock.
    /// </summary>
    /// <remarks>
    /// Single-digit fields and surrounding spaces are accepted, as they are on
    /// macOS. A date that does not exist — the 30th of February, 24:00 — is
    /// refused rather than rolled into a different day, so it can only ever
    /// arrive as "no date given". A time the clocks skip lands the length of
    /// the jump later, and a time that happens twice is read as its second
    /// occurrence, which is what the macOS date reader does with both.
    /// </remarks>
    internal static DateTimeOffset? ParseDue(string text, AppCalendar calendar)
    {
        if (!DateTime.TryParseExact(
                text,
                "yyyy-M-d'T'H:m",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowLeadingWhite | DateTimeStyles.AllowTrailingWhite,
                out var local))
        {
            return null;
        }
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (calendar.TimeZone.IsAmbiguousTime(local))
        {
            var later = calendar.TimeZone.GetAmbiguousTimeOffsets(local).Min();
            return new DateTimeOffset(local, later);
        }
        return calendar.FromLocal(local);
    }

    private async Task<string> SendAsync(
        string system,
        string user,
        IReadOnlyList<(string Role, string Content)> history,
        CancellationToken cancellationToken)
    {
        if (!settings.IsEnabled)
        {
            throw new AssistantException(AssistantError.NotConfigured);
        }
        if (settings.ResolvedUrl is not { } baseUrl)
        {
            throw new AssistantException(AssistantError.InvalidEndpoint);
        }
        if (settings.Provider.RequiresApiKey() && string.IsNullOrEmpty(apiKey))
        {
            throw new AssistantException(AssistantError.MissingApiKey);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                AssistantEndpoint.RequestUri(baseUrl, "chat/completions"));
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            }
            request.Content = AssistantJson.Content(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("model", settings.Model);
                writer.WriteNumber("temperature", 0.2);
                writer.WriteStartArray("messages");
                WriteMessage(writer, "system", system);
                foreach (var turn in history)
                {
                    WriteMessage(writer, turn.Role, turn.Content);
                }
                WriteMessage(writer, "user", user);
                writer.WriteEndArray();
                writer.WriteEndObject();
            });

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 401 or 403)
            {
                throw new AssistantException(AssistantError.Unauthorised);
            }
            if (status == 429)
            {
                throw new AssistantException(AssistantError.RateLimited);
            }
            if (status is < 200 or > 299)
            {
                throw new AssistantException(AssistantError.BadResponse);
            }

            var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            return ReplyContent(data) ?? throw new AssistantException(AssistantError.BadResponse);
        }
        catch (AssistantException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new AssistantException(AssistantError.Cancelled);
        }
        catch (Exception)
        {
            // Refused, timed out, not an address a request can go to: as on
            // macOS, all of these read as "could not be reached".
            throw new AssistantException(AssistantError.Unreachable);
        }
    }

    private static void WriteMessage(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    /// <summary>
    /// The first choice's message text, or null when the body is not a
    /// completion this app can read.
    /// </summary>
    private static string? ReplyContent(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data, AssistantJson.DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !AssistantJson.TryGetFirst(root, "choices", out var choices)
                || !AssistantJson.IsArrayOfObjects(choices)
                || choices.GetArrayLength() == 0
                || !AssistantJson.TryGetFirst(choices[0], "message", out var message)
                || message.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return AssistantJson.StringValue(message, "content");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Keeps assistant API keys beside the feed URL in the secret store, so none
/// is ever written to preferences, the conversation or diagnostics.
/// </summary>
/// <remarks>
/// One key per saved model, under an account derived from that model's id —
/// <c>assistant-api-key.</c> followed by the id in capitals, exactly as macOS
/// names its Keychain items. Keeping them apart is what lets someone hold keys
/// for two hosted services at once and switch between them, rather than
/// pasting one over the other. A null id addresses the single account earlier
/// versions used, which is what makes the move to named models invisible to
/// anyone upgrading. Operations run one at a time, so moving the old key
/// cannot interleave with a save.
/// </remarks>
public sealed class AssistantKeyStore
{
    private readonly ISecretStore secrets;
    private readonly string account;
    private readonly SemaphoreSlim gate = new(1, 1);

    public AssistantKeyStore(ISecretStore secrets, string account = SecretAccounts.AssistantApiKey)
    {
        this.secrets = secrets;
        this.account = account;
    }

    /// <summary>The account a profile's key is kept under.</summary>
    public static string AccountFor(Guid? profileId, string account = SecretAccounts.AssistantApiKey)
        => profileId is { } id ? account + "." + AssistantText.UuidString(id) : account;

    /// <summary>Stores the key trimmed. An empty key removes the stored one instead.</summary>
    public async Task SaveAsync(string key, Guid? profileId = null)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveUnlockedAsync(key, profileId).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The stored key, or null when there is none.</summary>
    public async Task<string?> LoadAsync(Guid? profileId = null)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await secrets.ReadAsync(AccountFor(profileId, account)).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(Guid? profileId = null)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await secrets.DeleteAsync(AccountFor(profileId, account)).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Moves the single key earlier versions stored onto a named model, then
    /// takes the old item away so there is one home for it rather than two.
    /// </summary>
    public async Task AdoptLegacyKeyAsync(Guid profileId)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var legacy = await secrets.ReadAsync(AccountFor(null, account)).ConfigureAwait(false);
            if (string.IsNullOrEmpty(legacy))
            {
                return;
            }
            await SaveUnlockedAsync(legacy, profileId).ConfigureAwait(false);
            await secrets.DeleteAsync(AccountFor(null, account)).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SaveUnlockedAsync(string key, Guid? profileId)
    {
        var trimmed = key.Trim();
        if (trimmed.Length == 0)
        {
            await secrets.DeleteAsync(AccountFor(profileId, account)).ConfigureAwait(false);
            return;
        }
        await secrets.WriteAsync(AccountFor(profileId, account), trimmed).ConfigureAwait(false);
    }
}
