using System.Net;
using System.Text;
using System.Text.Json;

namespace CanvasCountdown.Tests.Assistant;

/// <summary>
/// What is sent to the local model server and how its answers are read. No
/// request leaves the test.
/// </summary>
public sealed class OllamaModelManagerTests
{
    private static readonly Uri ChatAddress = new("http://localhost:11434/v1");

    /// <summary>
    /// A manager with the real timeouts, except where a test is about a
    /// timeout and asks for a short one so it does not wait seconds.
    /// </summary>
    private static OllamaModelManager Manager(
        HttpMessageHandler handler,
        TimeSpan? requestTimeout = null,
        TimeSpan? reachabilityTimeout = null)
        => new(
            handler,
            requestTimeout ?? OllamaModelManager.RequestTimeout,
            reachabilityTimeout ?? OllamaModelManager.ReachabilityTimeout,
            OllamaModelManager.DownloadTimeout);

    private static FakeHttpHandler Streaming(Func<Stream> body, HttpStatusCode status = HttpStatusCode.OK)
        => new(_ => new HttpResponseMessage(status) { Content = new StreamContent(body()) });

    private static Stream Lines(params string[] lines) => new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));

    [Theory]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/")]
    [InlineData("http://localhost:11434/v1//", "http://localhost:11434/")]
    [InlineData("http://localhost:11434/api/v1", "http://localhost:11434/api/")]
    [InlineData("http://localhost:11434/V1", "http://localhost:11434/V1")]
    [InlineData("http://localhost:11434/v1?tenant=invented", "http://localhost:11434/?tenant=invented")]
    [InlineData("http://localhost:11434/v%31", "http://localhost:11434/")]
    public void TheApiRootIsTheChatAddressWithoutItsV1(string chatAddress, string root)
    {
        Assert.Equal(root, OllamaModelManager.ApiRoot(new Uri(chatAddress)).AbsoluteUri);
    }

    // Is it there?

    [Fact]
    public async Task ReachabilityAsksForTheTagList()
    {
        var handler = FakeHttpHandler.Replying(HttpStatusCode.OK, """{"models":[]}""");

        Assert.True(await Manager(handler).IsReachableAsync(ChatAddress));
        Assert.Equal(HttpMethod.Get, handler.Single.Method);
        Assert.Equal("http://localhost:11434/api/tags", handler.Single.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task AServerThatFailsOrIsAbsentOrSlowIsNotReachable()
    {
        Assert.False(await Manager(FakeHttpHandler.Replying(HttpStatusCode.ServiceUnavailable, "")).IsReachableAsync(ChatAddress));
        Assert.False(await Manager(new RefusingHandler()).IsReachableAsync(ChatAddress));
        Assert.False(await Manager(FakeHttpHandler.Hanging(), reachabilityTimeout: TimeSpan.FromMilliseconds(100)).IsReachableAsync(ChatAddress));
    }

    [Fact]
    public async Task AnAddressNoRequestCanGoToIsNeverAsked()
    {
        var handler = FakeHttpHandler.Replying(HttpStatusCode.OK, """{"models":[]}""");

        Assert.False(await Manager(handler).IsReachableAsync(new Uri("not-an-address", UriKind.Relative)));
        Assert.False(await Manager(handler).IsReachableAsync(new Uri("ftp://localhost/v1")));
        Assert.Empty(handler.Requests);
    }

    // What is installed

    [Fact]
    public async Task InstalledModelsAreReadAndSortedByName()
    {
        var handler = FakeHttpHandler.Replying(
            HttpStatusCode.OK,
            """
            {"models":[
              {"name":"qwen2.5:7b","size":4683087332,"digest":"invented"},
              {"name":"llama3.2:3b","size":2.0e9},
              {"size":1},
              {"name":"mistral:7b"},
              {"name":"Tiny","size":true}
            ]}
            """);

        var models = await Manager(handler).InstalledModelsAsync(ChatAddress);

        Assert.Equal(["Tiny", "llama3.2:3b", "mistral:7b", "qwen2.5:7b"], models.Select(model => model.Name));
        Assert.Equal([1L, 2_000_000_000L, 0L, 4_683_087_332L], models.Select(model => model.SizeBytes));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"models":[]}""", LocalModelError.ServerUnreachable)]
    [InlineData(HttpStatusCode.OK, "not json", LocalModelError.RequestFailed)]
    [InlineData(HttpStatusCode.OK, """{"models":"none"}""", LocalModelError.RequestFailed)]
    [InlineData(HttpStatusCode.OK, """{"models":[{"name":"a"},1]}""", LocalModelError.RequestFailed)]
    [InlineData(HttpStatusCode.OK, """{"tags":[]}""", LocalModelError.RequestFailed)]
    public async Task AListTheServerCannotGiveIsReportedAsSuch(HttpStatusCode status, string body, LocalModelError expected)
    {
        var error = await Assert.ThrowsAsync<LocalModelException>(
            () => Manager(FakeHttpHandler.Replying(status, body)).InstalledModelsAsync(ChatAddress));

        Assert.Equal(expected, error.Error);
    }

    [Fact]
    public async Task AnAbsentServerCannotListAnything()
    {
        var error = await Assert.ThrowsAsync<LocalModelException>(
            () => Manager(new RefusingHandler()).InstalledModelsAsync(ChatAddress));

        Assert.Equal(LocalModelError.ServerUnreachable, error.Error);
    }

    // Downloading

    [Fact]
    public async Task APullStreamsItsProgressAndEndsComplete()
    {
        var handler = Streaming(() => Lines(
            """{"status":"pulling manifest"}""",
            """{"status":"downloading","total":200,"completed":50}""",
            "not a progress line",
            "",
            """{"status":"downloading","total":200,"completed":250}""",
            """{"status":"verifying","total":0,"completed":0}""",
            """{"status":"success"}"""));
        var progress = new RecordingProgress();

        await Manager(handler).PullAsync("llama3.2:3b", ChatAddress, progress);

        var request = handler.Single;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://localhost:11434/api/pull", request.Uri.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("""{"model":"llama3.2:3b","stream":true}""", request.Body);
        Assert.Equal([0.25, 1, 1], progress.Values);
    }

    [Fact]
    public async Task APullThatReportsAnErrorFails()
    {
        var handler = Streaming(() => Lines(
            """{"status":"pulling manifest","total":10,"completed":1}""",
            """{"error":"pull model manifest: file does not exist"}"""));
        var progress = new RecordingProgress();

        var error = await Assert.ThrowsAsync<LocalModelException>(
            () => Manager(handler).PullAsync("no-such-model", ChatAddress, progress));

        Assert.Equal(LocalModelError.RequestFailed, error.Error);
        Assert.Equal([0.1], progress.Values);
    }

    [Fact]
    public async Task APullTheServerRefusesIsUnreachable()
    {
        var refused = await Assert.ThrowsAsync<LocalModelException>(
            () => Manager(Streaming(() => Lines(), HttpStatusCode.NotFound)).PullAsync("m", ChatAddress, new RecordingProgress()));
        var absent = await Assert.ThrowsAsync<LocalModelException>(
            () => Manager(new RefusingHandler()).PullAsync("m", ChatAddress, new RecordingProgress()));

        Assert.Equal(LocalModelError.ServerUnreachable, refused.Error);
        Assert.Equal(LocalModelError.ServerUnreachable, absent.Error);
    }

    [Fact]
    public async Task APullThatStallsGivesUpAsUnreachable()
    {
        var handler = Streaming(() => new StallingStream("""{"total":100,"completed":50}""" + "\n"));
        var progress = new RecordingProgress();

        var error = await Assert.ThrowsAsync<LocalModelException>(
            () => Manager(handler, requestTimeout: TimeSpan.FromMilliseconds(500)).PullAsync("m", ChatAddress, progress));

        Assert.Equal(LocalModelError.ServerUnreachable, error.Error);
        Assert.Equal([0.5], progress.Values);
    }

    [Fact]
    public async Task CancellingAPullSaysSo()
    {
        var handler = Streaming(() => new StallingStream("""{"total":100,"completed":50}""" + "\n"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAsync<LocalModelException>(
            () => Manager(handler).PullAsync("m", ChatAddress, new RecordingProgress(), cancellation.Token));

        Assert.Equal(LocalModelError.Cancelled, error.Error);
    }

    // Removing

    [Fact]
    public async Task DeletingSendsTheModelName()
    {
        var handler = FakeHttpHandler.Replying(HttpStatusCode.OK, "");

        await Manager(handler).DeleteAsync("llama3.2:3b", ChatAddress);

        var request = handler.Single;
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("http://localhost:11434/api/delete", request.Uri.AbsoluteUri);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("llama3.2:3b", JsonDocument.Parse(request.Body!).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ADeleteThatDoesNotCompleteIsReported()
    {
        var missing = await Assert.ThrowsAsync<LocalModelException>(
            () => Manager(FakeHttpHandler.Replying(HttpStatusCode.NotFound, "")).DeleteAsync("m", ChatAddress));
        var absent = await Assert.ThrowsAsync<LocalModelException>(
            () => Manager(new RefusingHandler()).DeleteAsync("m", ChatAddress));

        Assert.Equal(LocalModelError.RequestFailed, missing.Error);
        Assert.Equal(LocalModelError.RequestFailed, absent.Error);
    }

    [Fact]
    public void TheTimeoutsAreTheMacOnes()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), OllamaModelManager.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), OllamaModelManager.ReachabilityTimeout);
        Assert.Equal(TimeSpan.FromHours(1), OllamaModelManager.DownloadTimeout);
    }

    [Fact]
    public void TheErrorsSayWhatHappened()
    {
        Assert.Equal("Ollama is not responding on this PC.", new LocalModelException(LocalModelError.ServerUnreachable).Message);
        Assert.Equal("That model operation did not complete.", new LocalModelException(LocalModelError.RequestFailed).Message);
        Assert.Equal("The download was cancelled.", new LocalModelException(LocalModelError.Cancelled).Message);
    }

    // What the panel shows

    [Fact]
    public async Task AServerThatAnswersButCannotListShowsNothingInstalled()
    {
        var handler = new FakeHttpHandler(request => request.Uri.AbsolutePath == "/api/tags" && request.Method == HttpMethod.Get
            ? FakeHttpHandler.Response(HttpStatusCode.OK, "not json")
            : FakeHttpHandler.Response(HttpStatusCode.NotFound, ""));

        var inventory = await LocalModelInventory.RefreshAsync(Manager(handler), ChatAddress);

        Assert.True(inventory.IsReachable);
        Assert.Empty(inventory.Installed);
    }

    [Fact]
    public async Task AnAddressThatCannotBeReadShowsTheServerAsAbsent()
    {
        var inventory = await LocalModelInventory.RefreshAsync(new StubModelManager(reachable: true), baseUrl: null);

        Assert.Equal(LocalModelInventory.Unreachable, inventory);
    }
}
