using System.Net;
using System.Text;

namespace CanvasCountdown.Tests.Feed;

public sealed class CanvasFeedFetcherTests
{
    private static readonly Uri FeedUrl = new("https://canvas.example.edu/feeds/calendar.ics?feed=private-token");

    /// <summary>Answers each request from a script, delivering bodies in fixed-size chunks.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requested.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>A body that arrives in chunks and counts how much of it was ever read.</summary>
    private sealed class ChunkedStream(int length, int chunkSize) : Stream
    {
        public int Delivered { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - Delivered;
            if (remaining <= 0)
            {
                return 0;
            }
            var size = Math.Min(Math.Min(count, chunkSize), remaining);
            Array.Fill(buffer, (byte)'A', offset, size);
            Delivered += size;
            return size;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => Delivered; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Ok(Stream body, long? declaredLength = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        response.Content.Headers.ContentLength = declaredLength;
        return response;
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    [Fact]
    public async Task NormalFeedIsDownloadedIntact()
    {
        var body = Encoding.UTF8.GetBytes(Ics.Full);
        using var fetcher = new CanvasFeedFetcher(new FakeHandler(_ => Ok(new MemoryStream(body))));

        Assert.Equal(body, await fetcher.FetchAsync(FeedUrl));
    }

    [Fact]
    public async Task BodyIsAccumulatedInDeliveredChunksNotPerByte()
    {
        using var fetcher = new CanvasFeedFetcher(new FakeHandler(_ => Ok(new ChunkedStream(256 * 1024, 16 * 1024))));

        var data = await fetcher.FetchAsync(FeedUrl);

        Assert.Equal(256 * 1024, data.Length);
        Assert.InRange(fetcher.LastResponseChunkCount, 1, 64);
    }

    [Fact]
    public async Task OversizedStreamIsRejectedAndStopsEarly()
    {
        var stream = new ChunkedStream(50 * 1024 * 1024, 64 * 1024);
        using var fetcher = new CanvasFeedFetcher(new FakeHandler(_ => Ok(stream)), maximumResponseSize: 1_048_576);

        var error = await Assert.ThrowsAsync<CanvasFeedFetchException>(() => fetcher.FetchAsync(FeedUrl));

        Assert.Equal(CanvasFeedFetchError.ResponseTooLarge, error.Error);
        Assert.InRange(stream.Delivered, 1_048_576, 1_048_576 + (128 * 1024));
    }

    [Fact]
    public async Task DeclaredContentLengthOverTheLimitIsRejectedBeforeAnyBody()
    {
        var stream = new ChunkedStream(10, 10);
        using var fetcher = new CanvasFeedFetcher(new FakeHandler(_ => Ok(stream, declaredLength: 20 * 1_048_576)), maximumResponseSize: 1_048_576);

        var error = await Assert.ThrowsAsync<CanvasFeedFetchException>(() => fetcher.FetchAsync(FeedUrl));

        Assert.Equal(CanvasFeedFetchError.ResponseTooLarge, error.Error);
        Assert.Equal(0, stream.Delivered);
        Assert.Equal("The calendar feed exceeds the 1 MB safety limit.", error.Message);
    }

    [Fact]
    public async Task InvalidHttpStatusIsReported()
    {
        using var fetcher = new CanvasFeedFetcher(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var error = await Assert.ThrowsAsync<CanvasFeedFetchException>(() => fetcher.FetchAsync(FeedUrl));

        Assert.Equal(CanvasFeedFetchError.HttpStatus, error.Error);
        Assert.Equal(403, error.Status);
        Assert.Equal("fetch.http.403", error.DiagnosticCode);
    }

    [Fact]
    public async Task EmptyResponseIsReported()
    {
        using var fetcher = new CanvasFeedFetcher(new FakeHandler(_ => Ok(new MemoryStream())));

        var error = await Assert.ThrowsAsync<CanvasFeedFetchException>(() => fetcher.FetchAsync(FeedUrl));

        Assert.Equal(CanvasFeedFetchError.EmptyResponse, error.Error);
    }

    [Fact]
    public async Task RedirectToPlainHttpIsRejected()
    {
        var handler = new FakeHandler(_ => Redirect("http://canvas.example.edu/plain.ics"));
        using var fetcher = new CanvasFeedFetcher(handler);

        var error = await Assert.ThrowsAsync<CanvasFeedFetchException>(() => fetcher.FetchAsync(FeedUrl));

        Assert.Equal(CanvasFeedFetchError.InsecureRedirect, error.Error);
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task RedirectToAnotherHttpsAddressIsFollowed()
    {
        var body = Encoding.UTF8.GetBytes(Ics.Full);
        var handler = new FakeHandler(request => request.RequestUri!.Host == "canvas.example.edu"
            ? Redirect("https://files.example.edu/feed.ics")
            : Ok(new MemoryStream(body)));
        using var fetcher = new CanvasFeedFetcher(handler);

        Assert.Equal(body, await fetcher.FetchAsync(FeedUrl));
        Assert.Equal(["canvas.example.edu", "files.example.edu"], handler.Requested.Select(uri => uri.Host));
    }

    [Fact]
    public async Task NonHttpsRequestIsRejectedBeforeAnyTransport()
    {
        var handler = new FakeHandler(_ => Ok(new MemoryStream([1])));
        using var fetcher = new CanvasFeedFetcher(handler);

        await Assert.ThrowsAsync<FeedUrlValidationException>(() => fetcher.FetchAsync(new Uri("http://canvas.example.edu/feed.ics")));

        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task NetworkFailureIsReportedWithoutLeakingTheUrl()
    {
        using var fetcher = new CanvasFeedFetcher(new FakeHandler(_ => throw new HttpRequestException("Connection to https://canvas.example.edu/feeds/calendar.ics?feed=private-token failed")));

        var error = await Assert.ThrowsAsync<CanvasFeedFetchException>(() => fetcher.FetchAsync(FeedUrl));

        Assert.Equal(CanvasFeedFetchError.NetworkFailure, error.Error);
        Assert.DoesNotContain("private-token", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", error.DiagnosticCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationStopsTheDownload()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var fetcher = new CanvasFeedFetcher(new FakeHandler(_ => Ok(new MemoryStream([1]))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync(FeedUrl, cancellation.Token));
    }

    [Theory]
    [InlineData("https://user:pass@canvas.example.edu/feed.ics", FeedUrlValidationError.EmbeddedCredentialsNotAllowed)]
    [InlineData("webcal://canvas.example.edu/feed.ics", FeedUrlValidationError.HttpsRequired)]
    [InlineData("http://canvas.example.edu/feed.ics", FeedUrlValidationError.HttpsRequired)]
    public void FeedAddressesAreCheckedBeforeUse(string address, FeedUrlValidationError expected)
        => Assert.Equal(expected, Assert.Throws<FeedUrlValidationException>(() => FeedUrlValidator.Validated(new Uri(address))).Error);

    [Theory]
    [InlineData("  webcal://canvas.example.edu/feeds/user_1.ics  ", "https://canvas.example.edu/feeds/user_1.ics")]
    [InlineData("https://canvas.example.edu/feeds/user_1.ics", "https://canvas.example.edu/feeds/user_1.ics")]
    public void PastedSubscribeLinksAreReadAsHttps(string typed, string expected)
        => Assert.Equal(new Uri(expected), FeedUrlInput.Normalize(typed));

    [Fact]
    public async Task TheStoredAddressRoundTripsAndAnInvalidOneIsRefused()
    {
        var secrets = new InMemorySecretStore();
        var store = new SecretFeedUrlStore(secrets);
        await store.SaveFeedUrlAsync(FeedUrl);
        Assert.Equal(FeedUrl, await store.LoadFeedUrlAsync());

        await secrets.WriteAsync(SecretAccounts.CanvasCalendarFeed, "http://downgraded.example.edu/feed");

        Assert.Equal(FeedUrlStoreError.StoredValueIsInvalid, (await Assert.ThrowsAsync<FeedUrlStoreException>(() => store.LoadFeedUrlAsync())).Error);
    }
}
