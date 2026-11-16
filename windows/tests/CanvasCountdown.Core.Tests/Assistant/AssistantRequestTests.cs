using System.Net;
using System.Text.Json;

namespace CanvasCountdown.Tests.Assistant;

/// <summary>
/// What goes over the wire to a chat completions service, word for word, and
/// how every way it can go wrong is reported. No request leaves the test.
/// </summary>
public sealed class AssistantRequestTests
{
    private static AppCalendar Calendar => AssistantFixtures.Sydney;

    private static readonly DateTimeOffset Now = AssistantFixtures.Date(2026, 7, 28, hour: 10);

    private static readonly Guid FirstId = Guid.Parse("4c1d0e9a-0000-4000-a000-000000000001");
    private static readonly Guid SecondId = Guid.Parse("4c1d0e9a-0000-4000-a000-000000000002");

    private static ChatCompletionsAssistantService Service(
        HttpMessageHandler handler,
        AssistantSettings? settings = null,
        string? apiKey = null)
        => new(settings ?? AssistantFixtures.EnabledLocal, apiKey, Calendar, handler);

    private static JsonElement Body(RecordedRequest request)
    {
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }

    private static List<(string Role, string Content)> Messages(RecordedRequest request)
        => Body(request).GetProperty("messages").EnumerateArray()
            .Select(message => (message.GetProperty("role").GetString()!, message.GetProperty("content").GetString()!))
            .ToList();

    private static AssistantAssignmentDigest Digest(string title, string? course, DateTimeOffset due)
        => new(AssistantFixtures.Item(title, course, due));

    // The shape of a request

    [Fact]
    public async Task AQuestionIsPostedInTheCommonShape()
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion("The essay.");

        await Service(handler).AnswerAsync("  what is most urgent?  ", [], [], Now);

        var request = handler.Single;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://localhost:11434/v1/chat/completions", request.Uri.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        Assert.Null(request.Authorization);

        var body = Body(request);
        Assert.Equal(["model", "temperature", "messages"], body.EnumerateObject().Select(property => property.Name));
        Assert.Equal("llama3.1:8b", body.GetProperty("model").GetString());
        Assert.Equal(0.2, body.GetProperty("temperature").GetDouble());
        var messages = Messages(request);
        Assert.Equal(["system", "user"], messages.Select(message => message.Role));
        Assert.Equal("what is most urgent?", messages[1].Content);
        Assert.All(
            body.GetProperty("messages").EnumerateArray(),
            message => Assert.Equal(["role", "content"], message.EnumerateObject().Select(property => property.Name)));
    }

    [Fact]
    public async Task TheQuestionPromptIsWordForWord()
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion("The essay.");

        await Service(handler).AnswerAsync(
            "what is most urgent?",
            [],
            [Digest("Essay", "DESN1500", AssistantFixtures.Date(2026, 7, 30, hour: 23, minute: 59))],
            Now);

        Assert.Equal(
            "You help a student manage coursework deadlines. Answer briefly and concretely, in the user's language. "
            + "Use only the figures and items given below; the counts are already correct, so do not recount them. "
            + "Never invent a deadline that is not listed. If something is not in the list, say so.\n\n"
            + "Figures:\nTotal upcoming: 1\nDue within 7 days: 1\nDue within 31 days: 1\nNearest: Essay, in 2 days\n\n"
            + "Items:\n- Essay (DESN1500), due in 2 days",
            Messages(handler.Single)[0].Content);
    }

    [Fact]
    public async Task AnEmptyListIsSaidToBeNone()
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion("Nothing is due.");

        await Service(handler).AnswerAsync("anything due?", [], [], Now);

        Assert.EndsWith("Items:\nnone", Messages(handler.Single)[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheLastSixTurnsGoWithAQuestion()
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion("Yes.");
        var history = Enumerable.Range(0, 8)
            .Select(index => new AssistantMessage
            {
                Role = index % 2 == 0 ? AssistantMessageRole.User : AssistantMessageRole.Assistant,
                Text = $"turn {index}",
                Date = Now.AddMinutes(index),
            })
            .ToList();

        await Service(handler).AnswerAsync("and after that?", history, [], Now);

        var messages = Messages(handler.Single);
        Assert.Equal(
            ["system", "user", "assistant", "user", "assistant", "user", "assistant", "user"],
            messages.Select(message => message.Role));
        Assert.Equal(
            ["turn 2", "turn 3", "turn 4", "turn 5", "turn 6", "turn 7", "and after that?"],
            messages.Skip(1).Select(message => message.Content));
    }

    [Fact]
    public async Task AtMostFortyItemsGoWithAQuestionInDueOrder()
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion("Plenty.");
        var digests = Enumerable.Range(0, 45)
            .Reverse()
            .Select(index => Digest($"Item {index:00}", null, Now.AddHours(index + 1)))
            .ToList();

        await Service(handler).AnswerAsync("what is due?", [], digests, Now);

        var items = Messages(handler.Single)[0].Content.Split("Items:\n")[1].Split('\n');
        Assert.Equal(40, items.Length);
        Assert.StartsWith("- Item 00,", items[0], StringComparison.Ordinal);
        Assert.StartsWith("- Item 39,", items[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyQuestionAsksNothing()
    {
        var refusing = new RefusingHandler();

        var answer = await Service(refusing).AnswerAsync(" \n ", [], [], Now);

        Assert.Equal(string.Empty, answer);
        Assert.Equal(0, refusing.Calls);
    }

    [Fact]
    public async Task TheSummaryPromptIsWordForWordAndListsEverything()
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion("Start the essay.");
        var digests = Enumerable.Range(0, 45)
            .Select(index => Digest($"Item {index:00}", null, Now.AddDays(45 - index)))
            .ToList();

        await Service(handler).SummariseAsync(digests, Now);

        var messages = Messages(handler.Single);
        Assert.Equal(
            "You help a student plan coursework. Be brief and concrete. Never invent deadlines that are not listed.",
            messages[0].Content);
        var expectedLines = Enumerable.Range(0, 45).Reverse().Select(index => $"- Item {index:00}, due in {45 - index} days");
        Assert.Equal(
            "Here is what is due:\n" + string.Join("\n", expectedLines) + "\n\nIn at most four sentences, say what to start first and why.",
            messages[1].Content);
    }

    [Fact]
    public async Task TheDraftPromptIsWordForWordAndSaysWhatDayItIsHere()
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion("""{"tasks":[]}""");
        // Half past three in the afternoon in UTC is half past one the next
        // morning in Sydney: "today" is the 28th.
        var now = new DateTimeOffset(2026, 7, 27, 15, 30, 0, TimeSpan.Zero);

        await Service(handler).DraftTasksAsync("  essay draft due friday 5pm  ", now);

        var messages = Messages(handler.Single);
        Assert.Equal(
            "Extract tasks with deadlines. Reply with JSON only, as "
            + "{\"tasks\":[{\"title\":\"...\",\"course\":null,\"due\":\"YYYY-MM-DDTHH:MM\"}]}. "
            + "Today is 2026-07-28. Use 23:59 when no time is given. If no deadline is stated, use null for due.",
            messages[0].Content);
        Assert.Equal("essay draft due friday 5pm", messages[1].Content);
    }

    [Fact]
    public async Task TheRevisionPromptCarriesTheDraftsAsTheyStand()
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion("""{"tasks":[]}""");
        AssistantDraftTask[] drafts =
        [
            new() { Id = FirstId, Title = "Workout 1", CourseName = "Gym", DueDate = AssistantFixtures.Date(2026, 8, 3, hour: 23), SourceText = "gym" },
            new() { Id = SecondId, Title = "Workout \"2\"", DueDate = null, SourceText = "gym" },
        ];

        await Service(handler).ReviseDraftsAsync(drafts, "  put them under COMP2050 ", Now);

        var messages = Messages(handler.Single);
        Assert.Equal(
            "You are editing a list of draft tasks that already exists. Here it is as JSON:\n\n"
            + """{"tasks":[{"id":"4C1D0E9A-0000-4000-A000-000000000001","title":"Workout 1","course":"Gym","due":"2026-08-03T23:00"},{"id":"4C1D0E9A-0000-4000-A000-000000000002","title":"Workout \"2\"","course":null,"due":null}]}"""
            + "\n\nApply the change the user asks for and reply with JSON only, in that same shape: "
            + "{\"tasks\":[{\"id\":\"...\",\"title\":\"...\",\"course\":null,\"due\":\"YYYY-MM-DDTHH:MM\"}]}. "
            + "Return the whole list, not only what changed. Keep the id of every task you are keeping, so edits line up with what is on screen; "
            + "use a new id only for a task you are adding, and leave a task out only if the user asked for it to go. "
            + "Today is 2026-07-28. Use 23:59 when no time is given.",
            messages[0].Content);
        Assert.Equal("put them under COMP2050", messages[1].Content);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("  https://openrouter.ai/api/v1  ", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("http://localhost:1234/v1?tenant=invented", "http://localhost:1234/v1/chat/completions?tenant=invented")]
    public async Task TheAddressTakesExactlyOneSlash(string baseUrl, string expected)
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion("ok");

        await Service(handler, AssistantFixtures.EnabledLocal with { BaseUrl = baseUrl }).AnswerAsync("q", [], [], Now);

        Assert.Equal(expected, handler.Single.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task AKeyIsSentWheneverOneIsGiven()
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion("ok");

        await Service(handler, apiKey: "sk-invented").AnswerAsync("q", [], [], Now);

        Assert.Equal("Bearer sk-invented", handler.Single.Authorization);
    }

    [Fact]
    public async Task AnEmptyKeyForACloudModelIsRefusedBeforeAnythingIsSent()
    {
        var refusing = new RefusingHandler();

        var error = await Assert.ThrowsAsync<AssistantException>(
            () => Service(refusing, AssistantFixtures.EnabledCloud, apiKey: "").AnswerAsync("q", [], [], Now));

        Assert.Equal(AssistantError.MissingApiKey, error.Error);
        Assert.Equal(0, refusing.Calls);
    }

    // Reading the reply

    [Fact]
    public async Task TheAnswerIsReturnedAsItCame()
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion("  The essay, then the quiz.\n");

        Assert.Equal("  The essay, then the quiz.\n", await Service(handler).AnswerAsync("q", [], [], Now));
    }

    [Fact]
    public async Task TheFirstChoiceAndTheFirstOfTwoKeysIsTheAnswer()
    {
        var handler = FakeHttpHandler.Replying(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"first","content":"second"}},{"message":{"content":"other"}}]}""");

        Assert.Equal("first", await Service(handler).AnswerAsync("q", [], [], Now));
    }

    [Fact]
    public async Task ATrailingCommaInTheCompletionIsForgiven()
    {
        var handler = FakeHttpHandler.Replying(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"},},],}""");

        Assert.Equal("ok", await Service(handler).AnswerAsync("q", [], [], Now));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AssistantError.Unauthorised)]
    [InlineData(HttpStatusCode.Forbidden, AssistantError.Unauthorised)]
    [InlineData(HttpStatusCode.TooManyRequests, AssistantError.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, AssistantError.BadResponse)]
    [InlineData(HttpStatusCode.Found, AssistantError.BadResponse)]
    [InlineData(HttpStatusCode.InternalServerError, AssistantError.BadResponse)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AssistantError.BadResponse)]
    public async Task StatusesMapToTheErrorsTheMacShows(HttpStatusCode status, AssistantError expected)
    {
        var handler = FakeHttpHandler.Replying(status, AssistantFixtures.Completion("ignored"));

        var error = await Assert.ThrowsAsync<AssistantException>(() => Service(handler).AnswerAsync("q", [], [], Now));

        Assert.Equal(expected, error.Error);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":["x"]}""")]
    [InlineData("""{"choices":[{}]}""")]
    [InlineData("""{"choices":[{"message":"x"}]}""")]
    [InlineData("""{"choices":[{"message":{"content":["part"]}}]}""")]
    [InlineData("""{"choices":[{"message":{"content":"ok"}},"stray"]}""")]
    public async Task AnUnreadableCompletionIsABadResponse(string body)
    {
        var handler = FakeHttpHandler.Replying(HttpStatusCode.OK, body);

        var error = await Assert.ThrowsAsync<AssistantException>(() => Service(handler).AnswerAsync("q", [], [], Now));

        Assert.Equal(AssistantError.BadResponse, error.Error);
    }

    // When the request does not get through

    [Fact]
    public async Task ARefusedConnectionIsUnreachable()
    {
        var error = await Assert.ThrowsAsync<AssistantException>(
            () => Service(new RefusingHandler()).AnswerAsync("q", [], [], Now));

        Assert.Equal(AssistantError.Unreachable, error.Error);
    }

    [Fact]
    public async Task AServerThatNeverAnswersIsUnreachableNotCancelled()
    {
        var service = new ChatCompletionsAssistantService(
            AssistantFixtures.EnabledLocal,
            null,
            Calendar,
            FakeHttpHandler.Hanging(),
            TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAsync<AssistantException>(() => service.AnswerAsync("q", [], [], Now));

        Assert.Equal(AssistantError.Unreachable, error.Error);
    }

    [Fact]
    public async Task CancellingARequestSaysSo()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<AssistantException>(
            () => Service(FakeHttpHandler.Hanging()).AnswerAsync("q", [], [], Now, cancellation.Token));

        Assert.Equal(AssistantError.Cancelled, error.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://local host/v1")]
    public async Task AnAddressThatIsNotAnAddressIsInvalidAndNothingIsSent(string baseUrl)
    {
        var refusing = new RefusingHandler();

        var error = await Assert.ThrowsAsync<AssistantException>(
            () => Service(refusing, AssistantFixtures.EnabledLocal with { BaseUrl = baseUrl }).AnswerAsync("q", [], [], Now));

        Assert.Equal(AssistantError.InvalidEndpoint, error.Error);
        Assert.Equal(0, refusing.Calls);
    }

    /// <summary>
    /// A word, a scheme-less host or another protocol still reads as an
    /// address, as it does on macOS, but no request can go to it: it is
    /// reported as unreachable, and nothing reaches the transport.
    /// </summary>
    [Theory]
    [InlineData("localhost:11434/v1")]
    [InlineData("not a url at all")]
    [InlineData("ftp://localhost/v1")]
    public async Task AnAddressNoRequestCanGoToIsUnreachableAndNothingIsSent(string baseUrl)
    {
        var handler = FakeHttpHandler.ReplyingWithCompletion("ok");

        var error = await Assert.ThrowsAsync<AssistantException>(
            () => Service(handler, AssistantFixtures.EnabledLocal with { BaseUrl = baseUrl }).AnswerAsync("q", [], [], Now));

        Assert.Equal(AssistantError.Unreachable, error.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void TheRequestTimeoutIsTheMacOne()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ChatCompletionsAssistantService.RequestTimeout);
    }
}
