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

    private sealed class DownloadHandler(string loginResponse) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var body = request.RequestUri!.AbsolutePath.EndsWith("/auth/login") ? loginResponse : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}