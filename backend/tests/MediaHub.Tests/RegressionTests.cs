using System.Net;
using System.Text.Json;
using MediaHub.Core.Entities;
using MediaHub.Core.Releases;
using MediaHub.Infrastructure.Clients;
using MediaHub.Infrastructure.Options;
using MediaHub.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace MediaHub.Tests;

public class RegressionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(10001, false)]
    [InlineData(1, true)]
    [InlineData(null, true)]
    public void StockRequestIsValidatedByMvc(int? maxFiles, bool valid)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.MvcServiceCollectionExtensions.AddControllers(services);
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var context = new Microsoft.AspNetCore.Mvc.ActionContext(
            new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = provider },
            new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var validator = (Microsoft.AspNetCore.Mvc.ModelBinding.Validation.IObjectModelValidator)provider.GetService(
            typeof(Microsoft.AspNetCore.Mvc.ModelBinding.Validation.IObjectModelValidator))!;
        validator.Validate(context, null, "", new MediaHub.Api.Controllers.InspectStockRequest(maxFiles));
        Assert.Equal(valid, context.ModelState.IsValid);
    }

    [Fact]
    public void StockProbeSeparatesDeclaredBitRatesAndExtractsTrackFlags()
    {
        using var document = JsonDocument.Parse("""
            {"format":{"format_name":"matroska,webm","duration":"3600.5","bit_rate":"5000000"},"streams":[
              {"codec_type":"video","codec_name":"hevc","profile":"Main 10","width":1920,"height":1080,"pix_fmt":"yuv420p10le","avg_frame_rate":"24000/1001","color_transfer":"smpte2084"},
              {"codec_type":"audio","codec_name":"aac","profile":"LC","channels":2,"sample_rate":"48000","tags":{"language":"fra","title":"VF","BPS":"192000"},"disposition":{"default":1,"comment":1}},
              {"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"eng"},"disposition":{"forced":1}}
            ]}
            """);
        var file = StorageMediaInspector.ParseProbe("episode.mkv", 100, document.RootElement);
        Assert.Null(file.VideoBitRate);
        Assert.Equal(5000000, file.OverallBitRate);
        Assert.Equal(10, file.BitDepth);
        Assert.Equal("24000/1001", file.FrameRate);
        Assert.Equal("PQ / HDR", file.Hdr);
        Assert.Equal(TimeSpan.FromSeconds(3600.5), file.Duration);
        var audio = Assert.Single(file.AudioTracks);
        Assert.Equal(192000, audio.BitRate);
        Assert.Equal("2", audio.Channels);
        Assert.Equal(48000, audio.SampleRate);
        Assert.True(audio.IsDefault);
        Assert.True(audio.IsCommentary);
        Assert.True(Assert.Single(file.SubtitleTracks).IsForced);
    }

    [Fact]
    public void RichAudioMetadataDoesNotChangeTheOriginalSelectionLayout()
    {
        var audio = new StockAudioTrack(1, "fra", "aac", "stereo", 48000, "VF", false);
        var subtitles = new[] { new StockSubtitleTrack(1, "eng", "subrip", "Standard", false, false) };
        var file = new StockMediaFile("first.mkv", 100, null, null, null, null, null, null, [audio], subtitles);
        var legacy = JsonSerializer.Serialize(new { AudioTracks = new[] { new { audio.Index, audio.Language, audio.Codec, audio.Channels, audio.SampleRate, audio.Name, audio.IsAudioDescription } }, SubtitleTracks = subtitles });
        Assert.Equal(legacy, TrackSelectionService.LayoutSignature(file));
        Assert.Equal(legacy, TrackSelectionService.LayoutSignature(file with { AudioTracks = [audio with { BitRate = 192000, IsDefault = true }] }));
    }

    [Fact]
    public void TelegramTrackSelectionPaginatesAndKeepsMultipleSelections()
    {
        var file = new StockMediaFile("episode.mkv", 1, null, null, null, null, null, null,
            Enumerable.Range(1, 10).Select(index => new StockAudioTrack(index, "jpn", "aac", "2.0", 48000, null, index == 1)).ToList(),
            [new StockSubtitleTrack(1, "fra", "srt", "SDH", false, false), new StockSubtitleTrack(2, "eng", "srt", null, false, false)]);
        var session = new TelegramTrackSelection
        {
            SessionId = Guid.NewGuid(), FileJson = JsonSerializer.Serialize(file), AudioIndexesJson = "[2,3]",
            SubtitleIndexesJson = "[2]", Heading = "Episode", Page = 0
        };
        var message = TelegramTrackSelectionService.Render(session);
        var keyboard = JsonSerializer.Serialize(message.Keyboard);
        Assert.Contains("Audio retenu : #2, #3", message.Text);
        Assert.Contains("Sous-titres retenus : #2", message.Text);
        Assert.Contains("Page 1/2", message.Text);
        Assert.DoesNotContain($"t:{session.SessionId:N}:a1\"", keyboard);
        Assert.Contains($"t:{session.SessionId:N}:g", keyboard);
        session.Page = 1;
        var next = TelegramTrackSelectionService.Render(session);
        Assert.Contains("Page 2/2", next.Text);
        Assert.DoesNotContain($"t:{session.SessionId:N}:s1\"", JsonSerializer.Serialize(next.Keyboard));
        foreach (var action in new[] { "a2", "s2", "p1", "c", "g", "r" })
        {
            var callback = $"t:{session.SessionId:N}:{action}";
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(callback) <= 64);
            Assert.True(TelegramTrackSelectionService.TryParseCallback(callback, out var parsedId, out var parsedAction));
            Assert.Equal(session.SessionId, parsedId);
            Assert.Equal(action, parsedAction);
        }
        Assert.False(TelegramTrackSelectionService.TryParseCallback($"t:{session.SessionId:N}:a-1", out _, out _));
        Assert.False(TelegramTrackSelectionService.TryParseCallback($"t:{session.SessionId:N}:unknown", out _, out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"ok\":true,\"result\":{\"id\":123456,\"is_bot\":true}}", true)]
    [InlineData(HttpStatusCode.OK, "{\"ok\":true,\"result\":{}}", false)]
    [InlineData(HttpStatusCode.Unauthorized, "{}", false)]
    public async Task TelegramAvailabilityChecksBotWithoutSendingMessages(HttpStatusCode status, string response, bool available)
    {
        using var handler = new TelegramHandler { Status = status, Response = response };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") };
        var client = new TelegramClient(http, Options.Create(new TelegramOptions { BotToken = "123456:fake_token", ChatId = 123 }));
        if (available)
            await client.CheckConnectionAsync();
        else
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => client.CheckConnectionAsync());
            Assert.DoesNotContain("fake_token", error.Message);
            if (status == HttpStatusCode.Unauthorized)
                Assert.Equal(status, Assert.IsType<HttpRequestException>(error).StatusCode);
        }
        Assert.Equal("/bot123456:fake_token/getMe", handler.LastPath);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task TelegramTokenColonIsResolvedAsHttpsPath()
    {
        using var handler = new TelegramHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") };
        var client = new TelegramClient(http, Options.Create(new TelegramOptions { BotToken = "123456:fake_token", ChatId = 123 }));
        await client.SendAsync("Test", null, default);
        await client.UpdatesAsync(7, default);
        await client.AnswerAsync("callback", "OK", default);
        await client.EditAsync(1, "Done", default);
        Assert.Equal(4, handler.Requests);
    }

    private sealed class TelegramHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string Response { get; init; } = "{\"ok\":true,\"result\":{}}";
        public string? LastPath { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("api.telegram.org", request.RequestUri.Host);
            Assert.StartsWith("/bot123456:fake_token/", request.RequestUri.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(JsonValueKind.Object, body.RootElement.ValueKind);
            if (request.RequestUri.AbsolutePath.EndsWith("/sendMessage", StringComparison.Ordinal))
                Assert.False(body.RootElement.TryGetProperty("reply_markup", out _));
            Requests++;
            LastPath = request.RequestUri.AbsolutePath;
            return new HttpResponseMessage(Status) { Content = new StringContent(Response) };
        }
    }

    [Fact]
    public async Task WatchSearchRequestsCanOverlapWithoutSerializingResponses()
    {
        using var handler = new WatchOverlapHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var indexer = new TorznabClient(http, Options.Create(new TorznabOptions()), cache);
        await Task.WhenAll(indexer.SearchForWatchAsync(new MediaItem { TmdbId = 1 }),
            indexer.SearchForWatchAsync(new MediaItem { TmdbId = 2 }));
        Assert.Equal(2, handler.Peak);
        Assert.True(handler.StartSpacing >= TimeSpan.FromSeconds(TorznabClient.RequestSpacingSeconds));
    }

    private sealed class WatchOverlapHandler : HttpMessageHandler
    {
        private int active;
        private DateTimeOffset? firstStart;
        public TimeSpan StartSpacing { get; private set; }
        public int Peak { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var started = DateTimeOffset.UtcNow;
            if (firstStart is { } first) StartSpacing = started - first;
            else firstStart = started;
            Peak = Math.Max(Peak, Interlocked.Increment(ref active));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(6), ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"releases\":[]}") };
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    [Fact]
    public void WatchBaselineNeverCreatesAnApproval()
    {
        Assert.Equal(ReleaseDecision.Baseline, ReleaseWatchPolicy.InitialDecision(false, true));
        Assert.Equal(ReleaseDecision.Filtered, ReleaseWatchPolicy.InitialDecision(true, false));
        Assert.Equal(ReleaseDecision.Pending, ReleaseWatchPolicy.InitialDecision(true, true));
    }

    [Fact]
    public void WatchFingerprintSurvivesDownloadTokenRotation()
    {
        var first = new ReleaseCandidate { Title = "Film.2025.1080p", SizeBytes = 100, DownloadUrl = "https://example.test/a?token=first" };
        var second = new ReleaseCandidate { Title = "Film 2025 1080p", SizeBytes = 100, DownloadUrl = "https://example.test/a?token=second" };
        Assert.Equal(ReleaseWatchPolicy.Fingerprint(first), ReleaseWatchPolicy.Fingerprint(second));
        Assert.NotEqual(ReleaseWatchPolicy.Fingerprint(first), ReleaseWatchPolicy.Fingerprint(new ReleaseCandidate { Title = first.Title, SizeBytes = 101 }));
    }

    [Theory]
    [InlineData(123, 123, true)]
    [InlineData(456, 123, false)]
    [InlineData(123, 456, false)]
    [InlineData(123, -123, false)]
    public void WatchTelegramOnlyAuthorizesConfiguredPrivateUser(long sender, long chat, bool expected)
    {
        Assert.Equal(expected, new TelegramOptions { BotToken = "test", ChatId = 123 }.IsAuthorized(sender, chat));
        Assert.False(new TelegramOptions { ChatId = 123 }.IsAuthorized(sender, chat));
    }

    [Fact]
    public void WatchRejectsWrongMovieAndYear()
    {
        var media = new MediaItem { Title = "Alien", Year = 1979, Type = MediaHub.Core.MediaType.Movie };
        Assert.True(ReleaseWatchPolicy.Matches(media, new ReleaseCandidate { Title = "Alien.1979.1080p" }));
        Assert.False(ReleaseWatchPolicy.Matches(media, new ReleaseCandidate { Title = "Alien.1992.1080p" }));
        Assert.False(ReleaseWatchPolicy.Matches(media, new ReleaseCandidate { Title = "Aliens.1979.1080p" }));
    }

    [Fact]
    public void TrackPolicyDeduplicatesFrenchAndExcludesSdh()
    {
        var file = new StockMediaFile("input.mkv", 0, null, null, null, null, null, null,
            [new StockAudioTrack(1, "fra", "aac", "2", 48000, null, false)],
            [new StockSubtitleTrack(1, "fra", "SRT", "SDH", false, false),
             new StockSubtitleTrack(2, "fre", "SRT", "SRT", false, false),
             new StockSubtitleTrack(3, "eng", "SRT", "SRT", false, false)]);
        var recommendation = TrackSelectionPolicy.Recommend(file, "fr");
        Assert.Equal(new[] { 1 }, recommendation.AudioTrackIndexes);
        Assert.Equal(new[] { 2, 3 }, recommendation.SubtitleTrackIndexes);
        Assert.Empty(recommendation.MissingRequirements);
    }

    [Fact]
    public void MissingMountSentinelPreventsStorageSynchronization()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var options = new LibraryOptions { StorageRoot = directory.FullName, SentinelFile = ".mediahub-mounted" };
            Assert.Throws<DirectoryNotFoundException>(() => StorageAvailability.EnsureAvailable(options));
            File.WriteAllText(Path.Combine(directory.FullName, options.SentinelFile), "ready");
            StorageAvailability.EnsureAvailable(options);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("Fails.")]
    [InlineData("")]
    public async Task DownloadLoginRejectsHttpSuccessWithInvalidCredentials(string loginResponse)
    {
        using var handler = new DownloadHandler(loginResponse);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new QBittorrentClient(http, Options.Create(new QBittorrentOptions()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.GetStatusAsync("test"));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task DownloadLoginAcceptsConfirmedSuccess()
    {
        using var handler = new DownloadHandler("Ok.");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new QBittorrentClient(http, Options.Create(new QBittorrentOptions()));

        Assert.Null(await client.GetStatusAsync("test"));
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task DownloadLoginAcceptsNoContentWithVerifiedSession()
    {
        using var handler = new DownloadHandler("", HttpStatusCode.NoContent);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new QBittorrentClient(http, Options.Create(new QBittorrentOptions()));

        Assert.Null(await client.GetStatusAsync("test"));
        Assert.Equal(3, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task DownloadLoginRejectsNoContentWithoutVerifiedSession(HttpStatusCode versionStatus)
    {
        using var handler = new DownloadHandler("", HttpStatusCode.NoContent, versionStatus);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new QBittorrentClient(http, Options.Create(new QBittorrentOptions()));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStatusAsync("test"));
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public void TrackSelectionPreservesEpisodeNumbering()
    {
        var payload = new ReleasePayload(null, "Release", "/downloads/input.mkv", null, "hash",
            ReleaseSeasonNumber: 2, TmdbSeasonNumber: 1, FirstTmdbEpisodeNumber: 13);
        var serialized = JsonSerializer.Serialize(payload);
        var selected = JsonSerializer.Deserialize<ReleasePayload>(serialized)! with { AudioTrackIndexes = [1, 2] };
        var restored = JsonSerializer.Deserialize<ReleasePayload>(JsonSerializer.Serialize(selected))!;

        Assert.Equal(2, restored.ReleaseSeasonNumber);
        Assert.Equal(1, restored.TmdbSeasonNumber);
        Assert.Equal(13, restored.FirstTmdbEpisodeNumber);
        Assert.Equal(new[] { 1, 2 }, restored.AudioTrackIndexes);
    }

    [Theory]
    [InlineData("Film.1080p.MULTI.x264.WEB-DL", true)]
    [InlineData("Film_MULTI_1080p", true)]
    [InlineData("Multiplicity.1080p.x264.WEB-DL", false)]
    public void MultiDetectionUsesReleaseMarkers(string title, bool expected)
    {
        Assert.Equal(expected, ReleaseParser.Parse(new ReleaseCandidate { Title = title }).IsMulti);
    }

    private sealed class DownloadHandler(
        string loginResponse,
        HttpStatusCode loginStatus = HttpStatusCode.OK,
        HttpStatusCode versionStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            var isLogin = path.EndsWith("/auth/login");
            var isVersion = path.EndsWith("/app/version");
            var body = isLogin ? loginResponse : isVersion ? "v5.2.0" : "[]";
            var status = isLogin ? loginStatus : isVersion ? versionStatus : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}