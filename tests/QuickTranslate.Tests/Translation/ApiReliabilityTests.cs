using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuickTranslate.Core.Cache;
using QuickTranslate.Core.Options;
using QuickTranslate.Core.Translation;
using QuickTranslate.Infrastructure.Translation;
using Xunit;

namespace QuickTranslate.Tests.Translation;

public class ApiReliabilityTests
{
    private const string Secret = "sk-PRIVATE-KEY-SENTINEL";
    private const string Source = "PRIVATE-INPUT-SENTINEL";
    private static TranslationRequest Request => new(Source, null, "auto", "zh", TranslationMode.Block);
    private static AppSettings Settings(string extra = "") => JsonSerializer.Deserialize<AppSettings>(
        "{\"CustomLlmBaseUrl\":\"https://example.test/v1\",\"CustomLlmModel\":\"model\"" + extra + "}")!;

    private static CustomOpenAiTranslationProvider Provider(AppSettings settings,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeSpan? timeout = null)
        => new(new HttpClient(new Handler(send)), Options.Create(settings),
            NullLogger<CustomOpenAiTranslationProvider>.Instance, timeout);

    private static HttpResponseMessage Json(string text = "translated") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } }), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Sse(string value, bool stall = false) => new(HttpStatusCode.OK)
    {
        Content = stall ? new StreamContent(new StallStream(value)) : new StringContent(value, Encoding.UTF8, "text/event-stream")
    };

    private static async Task<List<TranslationChunk>> Collect(CustomOpenAiTranslationProvider provider, CancellationToken ct = default)
    {
        var result = new List<TranslationChunk>();
        await foreach (var chunk in provider.TranslateAsync(Request, ct)) result.Add(chunk);
        return result;
    }

    [Theory]
    [InlineData("https://host.test/v1/chat/completions", "https://host.test/v1/chat/completions")]
    [InlineData("https://host.test/v1/chat/completions/?api-version=1", "https://host.test/v1/chat/completions?api-version=1")]
    [InlineData("https://host.test/proxy/v1?api-version=1", "https://host.test/proxy/v1/chat/completions?api-version=1")]
    [InlineData("https://host.test", "https://host.test/chat/completions")]
    [InlineData("localhost.evil.test:11434/v1", "https://localhost.evil.test:11434/v1/chat/completions")]
    [InlineData("127.0.0.1.evil.test:11434/v1", "https://127.0.0.1.evil.test:11434/v1/chat/completions")]
    [InlineData("[::1]:11434/v1", "http://[::1]:11434/v1/chat/completions")]
    [InlineData("localhost/v1", "http://localhost/v1/chat/completions")]
    [InlineData("127.0.0.1/v1", "http://127.0.0.1/v1/chat/completions")]
    [InlineData("localhost:443/v1", "http://localhost:443/v1/chat/completions")]
    public void Endpoint_PreservesPathQueryAndAccurateLoopback(string input, string expected)
        => Assert.Equal(expected, CustomOpenAiTranslationProvider.BuildChatCompletionsUrl(input).AbsoluteUri);

    [Theory]
    [InlineData("https://user:password@host.test/v1")]
    [InlineData("https://host.test/v1#fragment")]
    [InlineData("ftp://host.test/v1")]
    [InlineData("")]
    public void Endpoint_RejectsUnsafeOrInvalidAddress(string input)
        => Assert.ThrowsAny<ArgumentException>(() => CustomOpenAiTranslationProvider.BuildChatCompletionsUrl(input));

    [Fact]
    public async Task AnonymousMode_OmitsExistingGlobalKey_AndSendsConfiguredPayload()
    {
        var settings = Settings(",\"CustomLlmRequireApiKey\":false,\"CustomLlmUseStreaming\":false,\"CustomLlmSendTemperature\":false");
        settings.ResolvedApiKey = Secret;
        string? auth = "not called";
        string? body = null;
        var provider = Provider(settings, async (request, ct) =>
        {
            auth = request.Headers.Authorization?.ToString();
            body = await request.Content!.ReadAsStringAsync(ct);
            return Json();
        });
        Assert.Single(await Collect(provider));
        Assert.Null(auth);
        using var payload = JsonDocument.Parse(body!);
        Assert.False(payload.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(payload.RootElement.TryGetProperty("temperature", out _));
        Assert.False(payload.RootElement.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task AnonymousMode_WithoutKey_ActuallyCallsServer()
    {
        int calls = 0;
        var provider = Provider(Settings(",\"CustomLlmRequireApiKey\":false"), (_, _) =>
        {
            calls++;
            return Task.FromResult(Json());
        });
        Assert.Equal("translated", (await Collect(provider)).Last().FullTranslation);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConnectionProbe_UsesAnonymousAndParameterSettings()
    {
        var settings = Settings(",\"CustomLlmRequireApiKey\":false,\"CustomLlmUseStreaming\":false,\"CustomLlmSendTemperature\":false");
        int calls = 0;
        var provider = Provider(settings, async (request, ct) =>
        {
            calls++;
            Assert.Null(request.Headers.Authorization);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.False(payload.RootElement.GetProperty("stream").GetBoolean());
            Assert.False(payload.RootElement.TryGetProperty("temperature", out _));
            Assert.False(payload.RootElement.TryGetProperty("max_tokens", out _));
            return Json("OK");
        });
        Assert.True((await provider.TestConnectionAsync("https://example.test/v1", "model", "")).Success);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyStall_MapsInternalTimeout_DespiteSuccessfulHeaders(bool sse)
    {
        var settings = Settings(); settings.ResolvedApiKey = Secret;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallStream("")) };
        response.Content.Headers.ContentType = new(sse ? "text/event-stream" : "application/json");
        var provider = Provider(settings, (_, _) => Task.FromResult(response), TimeSpan.FromMilliseconds(30));
        using var external = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var error = await Assert.ThrowsAsync<TranslationException>(() => Collect(provider, external.Token));
        Assert.Equal(TranslationErrorCode.Timeout, error.ErrorCode);
    }

    [Fact]
    public async Task BodyStall_ExternalCancellation_RemainsCancellation()
    {
        var settings = Settings(); settings.ResolvedApiKey = Secret;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallStream("")) };
        response.Content.Headers.ContentType = new("application/json");
        var provider = Provider(settings, (_, _) => Task.FromResult(response), TimeSpan.FromSeconds(2));
        using var external = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(provider, external.Token));
    }

    [Theory]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n")]
    public async Task TerminalSignal_CompletesWithoutWaitingForEof(string terminal)
    {
        var settings = Settings(); settings.ResolvedApiKey = Secret;
        var response = Sse("data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n\n" + terminal, true);
        response.Content.Headers.ContentType = new("text/event-stream");
        var provider = Provider(settings, (_, _) => Task.FromResult(response));
        using var external = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        Assert.Equal("你好", (await Collect(provider, external.Token)).Last().FullTranslation);
    }

    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\ndata: broken\n\ndata: [DONE]\n\n")]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}\n\n")]
    public async Task IncompleteMalformedEmptyOrTruncatedStream_IsInvalidResponse(string sse)
    {
        var settings = Settings(); settings.ResolvedApiKey = Secret;
        var provider = Provider(settings, (_, _) => Task.FromResult(Sse(sse)));
        var error = await Assert.ThrowsAsync<TranslationException>(() => Collect(provider));
        Assert.Equal(TranslationErrorCode.InvalidResponse, error.ErrorCode);
    }

    [Fact]
    public async Task Sse_MultilineDataAndHeartbeat_AreDecodedAsOneEvent()
    {
        var settings = Settings(); settings.ResolvedApiKey = Secret;
        var provider = Provider(settings, (_, _) => Task.FromResult(Sse(
            ": heartbeat\r\nevent: message\r\ndata: {\"choices\":\r\ndata: [{\"delta\":{\"content\":\"你好\"}}]}\r\n\r\ndata: [DONE]\r\n\r\n")));
        Assert.Equal("你好", (await Collect(provider)).Last().FullTranslation);
    }

    [Theory]
    [InlineData("{\"choices\":[{}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"lost\"}}]}")]
    [InlineData("{\"choices\":[{\"delta\":null}]}")]
    public async Task Sse_NonterminalChoiceWithoutDelta_RejectsPartialTranslation(string malformedFrame)
    {
        var settings = Settings(); settings.ResolvedApiKey = Secret;
        var provider = Provider(settings, (_, _) => Task.FromResult(Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"first \"}}]}\n\n" +
            "data: " + malformedFrame + "\n\ndata: [DONE]\n\n")));

        var error = await Assert.ThrowsAsync<TranslationException>(() => Collect(provider));
        Assert.Equal(TranslationErrorCode.InvalidResponse, error.ErrorCode);
    }

    [Theory]
    [InlineData("{\"choices\":[{\"delta\":{}}]}")]
    [InlineData("{\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}")]
    [InlineData("{\"choices\":[],\"usage\":{\"completion_tokens\":1}}")]
    [InlineData("{\"choices\":[{\"delta\":null,\"finish_reason\":\"stop\"}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\"}]}")]
    public async Task Sse_ValidEmptyDeltaRoleUsageAndTerminalFrames_PreserveTranslation(string validFrame)
    {
        var settings = Settings(); settings.ResolvedApiKey = Secret;
        var provider = Provider(settings, (_, _) => Task.FromResult(Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"first \"}}]}\n\n" +
            "data: " + validFrame + "\n\ndata: [DONE]\n\n")));

        Assert.Equal("first ", (await Collect(provider)).Last().FullTranslation);
    }

    [Theory]
    [InlineData(400, "application/json")]
    [InlineData(200, "application/json")]
    [InlineData(200, "text/event-stream")]
    public async Task ServiceEcho_IsAbsentFromEntireException(int status, string type)
    {
        var settings = Settings(); settings.ResolvedApiKey = Secret;
        var echo = Secret + " " + Source;
        var raw = JsonSerializer.Serialize(new { error = new { message = echo } });
        if (type == "text/event-stream") raw = "data: " + raw + "\n\n";
        var provider = Provider(settings, (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(raw, Encoding.UTF8, type) }));
        var error = await Assert.ThrowsAsync<TranslationException>(() => Collect(provider));
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Source, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CustomLlmRequireApiKey")]
    [InlineData("CustomLlmUseStreaming")]
    [InlineData("CustomLlmSendTemperature")]
    public void Flags_DefaultTrue_RoundtripApplyRevisionAndCacheIdentity(string propertyName)
    {
        var settings = Settings();
        var property = typeof(AppSettings).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.True((bool)property!.GetValue(settings)!);
        var revision = settings.TranslationSettingsRevision;
        var key = TranslationCacheKey.BuildForRequest("hello", "zh", TranslationMode.Block, settings, "custom");
        property.SetValue(settings, false);
        Assert.True(settings.TranslationSettingsRevision > revision);
        Assert.NotEqual(key, TranslationCacheKey.BuildForRequest("hello", "zh", TranslationMode.Block, settings, "custom"));
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        var target = new AppSettings(); target.ApplyFrom(copy);
        Assert.False((bool)property.GetValue(target)!);
        settings.ResolvedApiKey = Secret;
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(settings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnonymousMode_RejectsClientDefaultAuthorizationWithoutSending()
    {
        int calls = 0;
        var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            return Task.FromResult(Json());
        }));
        http.DefaultRequestHeaders.Authorization = new("Bearer", Secret);
        var provider = new CustomOpenAiTranslationProvider(http,
            Options.Create(Settings(",\"CustomLlmRequireApiKey\":false")),
            NullLogger<CustomOpenAiTranslationProvider>.Instance);
        var error = await Assert.ThrowsAsync<TranslationException>(() => Collect(provider));
        Assert.Equal(TranslationErrorCode.BadRequest, error.ErrorCode);
        Assert.Equal(0, calls);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedResponse_DoesNotExposePayloadThroughInnerException()
    {
        var settings = Settings(); settings.ResolvedApiKey = Secret;
        var provider = Provider(settings, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(Secret + Source, Encoding.UTF8, "application/json") }));
        var error = await Assert.ThrowsAsync<TranslationException>(() => Collect(provider));
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Source, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorResponse_IsNotRead_AndIsDisposed()
    {
        var settings = Settings(); settings.ResolvedApiKey = Secret;
        var stream = new StallStream(Secret + Source);
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StreamContent(stream) };
        var provider = Provider(settings, (_, _) => Task.FromResult(response));
        var error = await Assert.ThrowsAsync<TranslationException>(() => Collect(provider));
        Assert.Equal(TranslationErrorCode.BadRequest, error.ErrorCode);
        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ConsumerStoppingAfterFirstDelta_DisposesResponseStream()
    {
        var settings = Settings(); settings.ResolvedApiKey = Secret;
        var stream = new StallStream("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new("text/event-stream");
        var provider = Provider(settings, (_, _) => Task.FromResult(response));
        var chunks = provider.TranslateAsync(Request, CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await chunks.MoveNextAsync());
        await chunks.DisposeAsync();
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task OversizedSseEvent_IsRejectedAndDisposed()
    {
        var settings = Settings(); settings.ResolvedApiKey = Secret;
        var stream = new StallStream("data: " + new string('x', 300_000));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new("text/event-stream");
        var provider = Provider(settings, (_, _) => Task.FromResult(response));
        var error = await Assert.ThrowsAsync<TranslationException>(() => Collect(provider));
        Assert.Equal(TranslationErrorCode.InvalidResponse, error.ErrorCode);
        Assert.True(stream.BytesRead < 300_000);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task QwenBodyStall_UsesSharedFullRequestTimeout()
    {
        var stream = new StallStream("");
        var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(stream) })));
        var provider = new QwenMtTranslationProvider(http, Options.Create(new QwenMtOptions
        { ApiKey = Secret, Timeout = TimeSpan.FromMilliseconds(30) }), NullLogger<QwenMtTranslationProvider>.Instance);
        using var external = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var error = await Assert.ThrowsAsync<TranslationException>(async () =>
        { await foreach (var _ in provider.TranslateAsync(Request, external.Token)) { } });
        Assert.Equal(TranslationErrorCode.Timeout, error.ErrorCode);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task ApplyFrom_RequestSnapshotNeverMixesEndpointModelCredentialsAndFlags()
    {
        var first = Settings(); first.ResolvedApiKey = "first";
        var second = Settings(",\"CustomLlmRequireApiKey\":false,\"CustomLlmUseStreaming\":false,\"CustomLlmSendTemperature\":false");
        second.CustomLlmBaseUrl = "https://second.test/v1"; second.CustomLlmModel = "second"; second.ResolvedApiKey = "second";
        var target = new AppSettings(); target.ApplyFrom(first);
        var writer = Task.Run(() => { for (int i = 0; i < 10_000; i++) target.ApplyFrom((i & 1) == 0 ? first : second); });
        for (int i = 0; i < 10_000; i++)
        {
            var snapshot = target.CaptureTranslationRequestSettings();
            if (snapshot.ApiKey == "first")
            {
                Assert.Equal(first.CustomLlmBaseUrl, snapshot.Settings.BaseUrl);
                Assert.Equal("model", snapshot.Settings.Model);
                Assert.True(snapshot.Settings.RequireApiKey && snapshot.Settings.UseStreaming && snapshot.Settings.SendTemperature);
            }
            else
            {
                Assert.Equal("second", snapshot.ApiKey);
                Assert.Equal(second.CustomLlmBaseUrl, snapshot.Settings.BaseUrl);
                Assert.Equal("second", snapshot.Settings.Model);
                Assert.False(snapshot.Settings.RequireApiKey || snapshot.Settings.UseStreaming || snapshot.Settings.SendTemperature);
            }
        }
        await writer;
        Assert.DoesNotContain("first", JsonSerializer.Serialize(target.CaptureTranslationRequestSettings()), StringComparison.Ordinal);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class StallStream(string prefix) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
        private int _position;
        public int BytesRead => _position;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _prefix.Length)
            {
                int size = Math.Min(buffer.Length, _prefix.Length - _position);
                _prefix.AsMemory(_position, size).CopyTo(buffer); _position += size; return size;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
