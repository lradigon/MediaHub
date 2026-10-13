using MediaHub.Core;
using MediaHub.Api.Controllers;
using MediaHub.Core.Abstractions;
using MediaHub.Core.Entities;
using MediaHub.Core.Releases;
using MediaHub.Infrastructure.Data;
using MediaHub.Infrastructure.Services;
using MediaHub.Infrastructure.Clients;
using MediaHub.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MediaHub.Worker;
using Npgsql;
using Xunit;

namespace MediaHub.Tests;

public class WatchDatabaseTests
{
    [WatchDatabaseFact]
    public async Task StockInspectionCachesAndReadsWithoutMountAndOnlyReprobesChangedFiles()
    {
        await using var database = await WatchDatabase.CreateAsync();
        using var fixture = new SelectionFixture();
        fixture.Job(database.MediaId, "first.mkv");
        fixture.Job(database.MediaId, "second.mkv");
        using var db = database.Open();
        (await db.MediaItems.SingleAsync()).StoragePath = fixture.RootPath;
        await db.SaveChangesAsync();
        var options = Options.Create(new LibraryOptions { StorageRoot = fixture.RootPath });
        var service = new StockInspectionService(db, fixture.Inspector, options);
        var requested = await service.RequestAsync(database.MediaId, 1, false, null, default);
        using (var other = database.Open())
        {
            var duplicate = await new StockInspectionService(other, fixture.Inspector, options).RequestAsync(database.MediaId, null, true, null, default);
            Assert.Equal(requested!.Generation, duplicate!.Generation);
            Assert.Equal(1, duplicate.MaxFiles);
        }
        async Task Tick()
        {
            using var fresh = database.Open();
            await new StockInspectionService(fresh, fixture.Inspector, options).ProcessNextAsync(default);
        }
        await Tick(); await Tick(); await Tick();
        Assert.Equal(1, fixture.ProbeCount);
        Assert.Equal(1, await db.StockFileInspections.CountAsync(value => value.Status == "Ready"));
        Assert.Equal(2, await db.StockFileInspections.CountAsync());
        var offline = fixture.RootPath + "-offline";
        Directory.Move(fixture.RootPath, offline);
        try
        {
            using var fresh = database.Open();
            var controller = new StockController(fresh, new StockInspectionService(fresh, fixture.Inspector, options));
            var cached = (await controller.Get(database.MediaId, default)).Value!;
            Assert.Equal(2, cached.Files.Count);
            Assert.Single(cached.Files, value => value.Details is not null);
            await new StockInspectionService(fresh, fixture.Inspector, options).RequestAsync(database.MediaId, null, false, null, default);
            await Tick();
            Assert.Equal("Failed", (await fresh.StockInspections.AsNoTracking().SingleAsync()).Status);
            Assert.Equal(1, await fresh.StockFileInspections.CountAsync(value => value.Status == "Ready" && value.IsPresent));
        }
        finally { Directory.Move(offline, fixture.RootPath); }
        using (var fresh = database.Open()) await new StockInspectionService(fresh, fixture.Inspector, options).RequestAsync(database.MediaId, null, false, null, default);
        await Tick(); await Tick(); await Tick();
        Assert.Equal(2, fixture.ProbeCount);
        using (var fresh = database.Open()) await new StockInspectionService(fresh, fixture.Inspector, options).RequestAsync(database.MediaId, null, false, null, default);
        await Tick();
        Assert.Equal(2, fixture.ProbeCount);
        File.AppendAllText(Path.Combine(fixture.RootPath, "first.mkv"), "changed");
        using (var fresh = database.Open()) await new StockInspectionService(fresh, fixture.Inspector, options).RequestAsync(database.MediaId, null, false, null, default);
        await Tick(); await Tick(); await Tick();
        Assert.Equal(3, fixture.ProbeCount);
        Assert.Equal(2, await db.StockFileInspections.CountAsync(value => value.Status == "Ready"));
    }

    [WatchDatabaseFact]
    public async Task StockInspectionKeepsPartialResultsAndCancellationDoesNotProbeOrDeleteJobs()
    {
        await using var database = await WatchDatabase.CreateAsync();
        using var fixture = new SelectionFixture();
        fixture.Job(database.MediaId, "bad.mkv");
        fixture.Job(database.MediaId, "good.mkv");
        using var db = database.Open();
        (await db.MediaItems.SingleAsync()).StoragePath = fixture.RootPath;
        await db.SaveChangesAsync();
        var options = Options.Create(new LibraryOptions { StorageRoot = fixture.RootPath });
        var service = new StockInspectionService(db, fixture.Inspector, options);
        async Task Tick()
        {
            using var fresh = database.Open();
            await new StockInspectionService(fresh, fixture.Inspector, options).ProcessNextAsync(default);
        }
        await service.RequestAsync(database.MediaId, null, false, null, default);
        await Tick(); await Tick(); await Tick(); await Tick();
        var scan = await db.StockInspections.AsNoTracking().SingleAsync();
        Assert.Equal("Completed", scan.Status);
        Assert.Equal(1, scan.FailedFiles);
        Assert.Equal(1, scan.InspectedFiles);
        Assert.Equal(1, await db.StockFileInspections.CountAsync(value => value.Status == "Ready"));
        var good = await db.StockFileInspections.AsNoTracking().SingleAsync(value => value.Status == "Ready");
        var foreign = new StockFileInspection { MediaItemId = database.MediaId, Path = "foreign.mkv", IsPresent = false };
        db.StockFileInspections.Add(foreign);
        await db.SaveChangesAsync();
        using (var fresh = database.Open())
        {
            var scoped = new StockInspectionService(fresh, fixture.Inspector, options);
            Assert.Null(await scoped.RequestAsync(database.MediaId, 1, true, foreign.Id, default));
            await scoped.RequestAsync(database.MediaId, 1, true, good.Id, default);
            await scoped.CancelAsync(database.MediaId, default);
        }
        var count = fixture.ProbeCount;
        await Tick();
        Assert.Equal(count, fixture.ProbeCount);
        Assert.Equal("Cancelled", (await db.StockInspections.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(good.DetailsJson, (await db.StockFileInspections.AsNoTracking().SingleAsync(value => value.Id == good.Id)).DetailsJson);
        Assert.Empty(await db.PipelineJobs.ToListAsync());
    }

    [WatchDatabaseFact]
    public async Task TelegramSelectionGroupConfirmsTenWithoutTenNotifications()
    {
        await using var database = await WatchDatabase.CreateAsync();
        using var fixture = new SelectionFixture();
        using var db = database.Open();
        (await db.MediaItems.SingleAsync()).OriginalLanguage = "ja";
        for (var index = 0; index < 10; index++) db.PipelineJobs.Add(fixture.Job(database.MediaId, $"episode-{index}.mkv"));
        await db.SaveChangesAsync();
        using var handler = new SelectionTelegramHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") };
        var telegram = new TelegramClient(http, Options.Create(new TelegramOptions { BotToken = "123:test", ChatId = 123 }));
        var service = new TelegramTrackSelectionService(db, new TrackSelectionService(db, fixture.Inspector), telegram);
        await service.NotifyNextAsync(default);
        await service.NotifyNextAsync(default);
        Assert.Equal(1, handler.Sends);
        var session = await db.TelegramTrackSelections.SingleAsync();
        var sessionId = session.SessionId;
        var messageId = session.MessageId!.Value;
        await service.HandleAsync(sessionId, messageId, "a3", default);
        using (var reopened = database.Open())
        {
            var resumed = new TelegramTrackSelectionService(reopened, new TrackSelectionService(reopened, fixture.Inspector), telegram);
            await resumed.NotifyNextAsync(default);
            await resumed.HandleAsync(sessionId, messageId, "s4", default);
        }
        Assert.Contains("interdite", await service.HandleAsync(sessionId, messageId, "a4", default));
        Assert.Contains("interdite", await service.HandleAsync(sessionId, messageId, "s3", default));
        using (var verify = database.Open())
        {
            var saved = await verify.TelegramTrackSelections.SingleAsync();
            Assert.Equal(new[] { 1, 2, 3 }, System.Text.Json.JsonSerializer.Deserialize<int[]>(saved.AudioIndexesJson));
            Assert.Equal(new[] { 1, 2, 4 }, System.Text.Json.JsonSerializer.Deserialize<int[]>(saved.SubtitleIndexesJson));
        }
        Assert.StartsWith("10 fichier", await service.HandleAsync(sessionId, messageId, "g", default));
        await service.NotifyNextAsync(default);
        Assert.Equal(1, handler.Sends);
        Assert.Equal(10, await db.PipelineJobs.CountAsync(job => job.Kind == JobKind.Encode));
        Assert.Equal(0, await db.PipelineJobs.CountAsync(job => job.Status == JobStatus.WaitingForSelection));
        Assert.Contains("plus active", await service.HandleAsync(sessionId, messageId, "g", default));
        foreach (var job in await db.PipelineJobs.Where(job => job.Kind == JobKind.Encode).ToListAsync())
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<ReleasePayload>(job.PayloadJson!)!;
            Assert.Equal(new[] { 1, 2, 3 }, payload.AudioTrackIndexes);
            Assert.Equal(new[] { 1, 2, 4 }, payload.SubtitleTrackIndexes);
            Assert.Equal(2, payload.ReleaseSeasonNumber);
            Assert.Equal(13, payload.FirstTmdbEpisodeNumber);
        }
    }

    [WatchDatabaseFact]
    public async Task TelegramSelectionRetiresWebValidatedMessageAndKeepsDifferentLayoutsWaiting()
    {
        await using var database = await WatchDatabase.CreateAsync();
        using var fixture = new SelectionFixture();
        using var db = database.Open();
        var first = fixture.Job(database.MediaId, "first.mkv");
        var different = fixture.Job(database.MediaId, "different.mkv");
        different.CreatedAt = first.CreatedAt.AddSeconds(1);
        db.PipelineJobs.AddRange(first, different);
        await db.SaveChangesAsync();
        using var handler = new SelectionTelegramHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") };
        var telegram = new TelegramClient(http, Options.Create(new TelegramOptions { BotToken = "123:test", ChatId = 123 }));
        var selections = new TrackSelectionService(db, fixture.Inspector);
        var service = new TelegramTrackSelectionService(db, selections, telegram);
        await service.NotifyNextAsync(default);
        var session = await db.TelegramTrackSelections.SingleAsync();
        var oldSessionId = session.SessionId;
        var oldMessageId = session.MessageId!.Value;
        var controller = new JobsController(db, selections);
        var response = await controller.ConfirmMatchingTrackSelections(first.Id,
            new MediaHub.Api.Contracts.ConfirmTrackSelectionRequest([1, 2], [1, 2]), default);
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(response.Result);
        Assert.Equal(1, await db.PipelineJobs.CountAsync(job => job.Kind == JobKind.Encode));
        Assert.Equal(1, await db.PipelineJobs.CountAsync(job => job.Status == JobStatus.WaitingForSelection));
        await service.NotifyNextAsync(default);
        Assert.Equal(2, handler.Sends);
        Assert.Equal(different.Id, (await db.TelegramTrackSelections.SingleAsync()).JobId);
        Assert.Contains("plus active", await service.HandleAsync(oldSessionId, oldMessageId, "c", default));
        Assert.Contains(handler.Edits, value => value.Contains("terminee ou annulee"));
        var nextSessionId = session.SessionId;
        var nextMessageId = session.MessageId!.Value;
        await db.PipelineJobs.Where(value => value.Id == different.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.Status, JobStatus.Cancelled));
        Assert.Contains("deja ete traite", await service.HandleAsync(nextSessionId, nextMessageId, "c", default));
        await service.NotifyNextAsync(default);
        Assert.Equal(2, handler.Sends);
        Assert.Equal(1, await db.PipelineJobs.CountAsync(job => job.Kind == JobKind.Encode));
    }

    [WatchDatabaseFact]
    public async Task TelegramSelectionRejectsEmptyAudioAndConcurrentConfirmationsCreateOneEncode()
    {
        await using var database = await WatchDatabase.CreateAsync();
        using var fixture = new SelectionFixture();
        using var db = database.Open();
        var job = fixture.Job(database.MediaId, "first.mkv");
        db.PipelineJobs.Add(job);
        await db.SaveChangesAsync();
        var selections = new TrackSelectionService(db, fixture.Inspector);
        Assert.NotNull((await selections.ConfirmAsync(job.Id, [], [], false, default)).Error);
        Assert.NotNull((await selections.ConfirmAsync(job.Id, [4], [], false, default)).Error);
        Assert.NotNull((await selections.ConfirmAsync(job.Id, [1], [3], false, default)).Error);
        Assert.NotNull((await selections.ConfirmAsync(job.Id, [1], [], false, default, "stale-layout")).Error);
        Assert.Empty(await db.PipelineJobs.Where(value => value.Kind == JobKind.Encode).ToListAsync());
        using var other = database.Open();
        var results = await Task.WhenAll(selections.ConfirmAsync(job.Id, [1, 2], [1], false, default),
            new TrackSelectionService(other, fixture.Inspector).ConfirmAsync(job.Id, [1, 2], [1], false, default));
        Assert.Equal(1, results.Sum(value => value.AppliedCount));
        Assert.Equal(1, await db.PipelineJobs.CountAsync(value => value.Kind == JobKind.Encode));
    }

    private sealed class SelectionFixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory();
        public string RootPath => directory.FullName;
        public int ProbeCount => File.Exists(Path.Combine(RootPath, "probe-count")) ? File.ReadAllLines(Path.Combine(RootPath, "probe-count")).Length : 0;
        public StorageMediaInspector Inspector { get; }
        public SelectionFixture()
        {
            var script = Path.Combine(directory.FullName, "ffprobe-fixture");
            var normal = "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\"},{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"channel_layout\":\"stereo\",\"tags\":{\"language\":\"jpn\"}},{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"tags\":{\"language\":\"fra\"}},{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"tags\":{\"language\":\"eng\"}},{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"tags\":{\"language\":\"fra\",\"title\":\"audio description\"}},{\"codec_type\":\"subtitle\",\"codec_name\":\"subrip\",\"tags\":{\"language\":\"fra\"}},{\"codec_type\":\"subtitle\",\"codec_name\":\"subrip\",\"tags\":{\"language\":\"eng\"}},{\"codec_type\":\"subtitle\",\"codec_name\":\"subrip\",\"tags\":{\"language\":\"fra\",\"title\":\"SDH\"}},{\"codec_type\":\"subtitle\",\"codec_name\":\"subrip\",\"tags\":{\"language\":\"spa\"}}]}";
            var different = normal.Replace("stereo", "5.1");
            File.WriteAllText(script, $"#!/bin/sh\nprintf 'probe\\n' >> '{Path.Combine(RootPath, "probe-count")}'\ncase \"$*\" in\n*bad.mkv*) exit 1 ;;\n*different.mkv*) printf '%s' '{different}' ;;\n*) printf '%s' '{normal}' ;;\nesac\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Inspector = new StorageMediaInspector(Options.Create(new FfprobeOptions { CliPath = script }));
        }
        public PipelineJob Job(Guid mediaId, string name)
        {
            var path = Path.Combine(directory.FullName, name);
            File.WriteAllText(path, "fixture");
            return new PipelineJob
            {
                MediaItemId = mediaId, Kind = JobKind.TrackSelection, Status = JobStatus.WaitingForSelection,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new ReleasePayload(null, name, path, null, null,
                    ReleaseSeasonNumber: 2, TmdbSeasonNumber: 1, FirstTmdbEpisodeNumber: 13))
            };
        }
        public void Dispose() => directory.Delete(true);
    }

    private sealed class SelectionTelegramHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }
        public List<string> Edits { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (request.RequestUri!.AbsolutePath.EndsWith("/sendMessage")) Sends++;
            else if (request.RequestUri.AbsolutePath.EndsWith("/editMessageText")) Edits.Add(body.RootElement.GetProperty("text").GetString()!);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"ok\":true,\"result\":{{\"message_id\":{Sends}}}}}")
            };
        }
    }

    [WatchDatabaseFact]
    public async Task SavedReleasesPersistCompleteSnapshotAndReplaceEmptyScan()
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new WatchIndexer();
        using (var db = database.Open())
            await new ReleaseWatchService(db, indexer).ScanAsync(database.MediaId, default);
        using (var db = database.Open())
        {
            var media = await db.MediaItems.SingleAsync();
            Assert.NotNull(media.ReleasesCheckedAt);
            var saved = Assert.Single(await new ReleaseWatchService(db, indexer).SavedReleasesAsync(media, default));
            Assert.Equal(indexer.Releases[0].Title, saved.Title);
            Assert.Equal(10, saved.Seeders);
            Assert.Equal("1080p", saved.Resolution);
            Assert.Equal(indexer.Releases[0].DownloadUrl, saved.DownloadUrl);
            Assert.Equal(1, indexer.Searches);
            indexer.Releases.Clear();
            await new ReleaseWatchService(db, indexer).ScanAsync(database.MediaId, default);
        }
        using var verify = database.Open();
        Assert.Empty(await new ReleaseWatchService(verify, indexer).SavedReleasesAsync(await verify.MediaItems.SingleAsync(), default));
        Assert.NotEmpty(await verify.ReleaseObservations.ToListAsync());
    }

    [WatchDatabaseFact]
    public async Task SavedReleasesSurviveFailedScan()
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new WatchIndexer();
        using var db = database.Open();
        var service = new ReleaseWatchService(db, indexer);
        await service.ScanAsync(database.MediaId, default);
        var before = await db.MediaItems.Select(media => media.ReleasesCheckedAt).SingleAsync();
        indexer.Failure = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => service.ScanAsync(database.MediaId, default));
        using var verify = database.Open();
        var media = await verify.MediaItems.SingleAsync();
        Assert.Equal(before, media.ReleasesCheckedAt);
        Assert.Single(await new ReleaseWatchService(verify, indexer).SavedReleasesAsync(media, default));
    }

    [WatchDatabaseFact]
    public async Task SavedReleasesEndpointNeverSearchesAndManualSearchPersists()
    {
        await using var database = await WatchDatabase.CreateAsync();
        using var db = database.Open();
        var indexer = new WatchIndexer();
        var service = new ReleaseWatchService(db, indexer);
        var controller = new MediaController(db, null!, null!, indexer, null!, null!, null!, null!, service);
        Assert.Empty((await controller.Releases(database.MediaId, null, default)).Value!.Releases);
        Assert.Equal(0, indexer.Searches);
        Assert.Single((await controller.SearchReleases(database.MediaId, null, default)).Value!.Releases);
        Assert.Equal(1, indexer.Searches);
        db.ChangeTracker.Clear();
        var saved = (await controller.Releases(database.MediaId, null, default)).Value!;
        Assert.Single(saved.Releases);
        Assert.NotNull(saved.CheckedAt);
        Assert.Equal(1, indexer.Searches);
    }

    [WatchDatabaseFact]
    public async Task SavedReleasesUseLegacyObservationsWithoutSearching()
    {
        await using var database = await WatchDatabase.CreateAsync();
        using var db = database.Open();
        var indexer = new WatchIndexer();
        var service = new ReleaseWatchService(db, indexer);
        await service.ScanAsync(database.MediaId, default);
        var media = await db.MediaItems.SingleAsync();
        media.ReleaseSnapshotJson = null;
        await db.SaveChangesAsync();
        Assert.Single(await service.SavedReleasesAsync(media, default));
        Assert.Equal(1, indexer.Searches);
        (await db.MediaWatches.SingleAsync()).LastCheckedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await db.SaveChangesAsync();
        Assert.Empty(await service.SavedReleasesAsync(media, default));
    }

    [WatchDatabaseFact]
    public async Task ManualScanCancellationInterruptsBlockedRequest()
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new ProgressWatchIndexer { Block = true };
        using var provider = ScanProvider(database, indexer);
        using var setup = database.Open();
        var scan = (await new WatchScanService(setup).RequestAsync(true, default))!;
        using var worker = new ReleaseWatchWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReleaseWatchWorker>.Instance);
        var running = worker.RunOnceAsync(default);
        await indexer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancel = database.Open();
        Assert.True(await new WatchScanService(cancel).CancelAsync(scan.Id, default));
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        using var verify = database.Open();
        Assert.Equal("cancelled", (await verify.WatchScans.SingleAsync()).State);
        Assert.Equal("cancelled", (await verify.WatchScanItems.SingleAsync()).State);
        Assert.Empty(await verify.ReleaseObservations.ToListAsync());
        Assert.Empty(await verify.PipelineJobs.ToListAsync());
    }

    [WatchDatabaseFact]
    public async Task ManualScanCancellationPreservesResultsAndQuota()
    {
        await using var database = await WatchDatabase.CreateAsync();
        using var db = database.Open();
        var service = new WatchScanService(db);
        var scan = (await service.RequestAsync(true, default))!;
        await db.WatchScans.Where(value => value.Id == scan.Id).ExecuteUpdateAsync(update => update
            .SetProperty(value => value.State, "paused").SetProperty(value => value.RetryAt, DateTimeOffset.UtcNow.AddHours(1)));
        Assert.True(await service.CancelAsync(scan.Id, default));
        Assert.True(await service.CancelAsync(scan.Id, default));
        db.ChangeTracker.Clear();
        Assert.Equal("cancelled", (await db.WatchScans.SingleAsync()).State);
        Assert.Equal("cancelled", (await db.WatchScanItems.SingleAsync()).State);
        Assert.Empty(await db.PipelineJobs.ToListAsync());
        var next = (await service.RequestAsync(true, default))!;
        Assert.NotEqual(scan.Id, next.Id);
        Assert.Equal("paused", next.State);
        Assert.True(next.RetryAt > DateTimeOffset.UtcNow.AddMinutes(59));
    }

    [WatchDatabaseFact]
    public async Task WatchScanWorkerRecordsRealPhasesAndRecoversInterruptedItems()
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new ProgressWatchIndexer();
        using var provider = ScanProvider(database, indexer);
        using var scope = provider.CreateScope();
        var scan = (await scope.ServiceProvider.GetRequiredService<WatchScanService>().RequestAsync(true, default))!;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.WatchScanItems.ExecuteUpdateAsync(update => update.SetProperty(value => value.State, "request"));
        using var worker = new ReleaseWatchWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReleaseWatchWorker>.Instance);
        await worker.RunOnceAsync(default);
        using var verify = database.Open();
        Assert.Equal("completed", (await verify.WatchScans.SingleAsync()).State);
        var item = await verify.WatchScanItems.SingleAsync();
        Assert.Equal("completed", item.State);
        Assert.NotNull(item.StartedAt);
        Assert.NotNull(item.FinishedAt);
        Assert.Equal(1, item.Found);
        Assert.Equal(1, item.Observations);
        Assert.Equal(0, item.NewProposals);
        Assert.Equal(new[] { "rate-limit", "request" }, indexer.Phases);
        Assert.Empty(await verify.PipelineJobs.ToListAsync());
    }

    [WatchDatabaseFact]
    public async Task WatchScanQuotaPauseCannotBeBypassedByManualRequest()
    {
        await using var database = await WatchDatabase.CreateAsync();
        using (var setup = database.Open())
        {
            setup.MediaItems.Add(new MediaItem { Title = "Another Series", Type = MediaType.Series });
            (await setup.WatchSettings.SingleAsync()).MaxParallelSearches = 1;
            await setup.SaveChangesAsync();
        }
        var indexer = new ProgressWatchIndexer { Quota = true };
        using var provider = ScanProvider(database, indexer);
        using var scope = provider.CreateScope();
        var scans = scope.ServiceProvider.GetRequiredService<WatchScanService>();
        var requested = (await scans.RequestAsync(true, default))!;
        using var worker = new ReleaseWatchWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReleaseWatchWorker>.Instance);
        await worker.RunOnceAsync(default);
        using var verify = database.Open();
        var paused = await verify.WatchScans.SingleAsync();
        Assert.Equal("paused", paused.State);
        Assert.True(paused.RetryAt > DateTimeOffset.UtcNow.AddMinutes(59));
        Assert.Equal(1, await verify.WatchScanItems.CountAsync(value => value.State == "failed"));
        Assert.Equal(1, await verify.WatchScanItems.CountAsync(value => value.State == "queued"));
        Assert.Equal(requested.Id, (await scans.RequestAsync(true, default))!.Id);
        await worker.RunOnceAsync(default);
        Assert.Equal(1, indexer.Searches);
        Assert.Empty(await verify.PipelineJobs.ToListAsync());
    }

    private static ServiceProvider ScanProvider(WatchDatabase database, ProgressWatchIndexer indexer) => new ServiceCollection()
        .AddScoped(_ => database.Open()).AddScoped<ReleaseWatchService>().AddScoped<WatchScanService>()
        .AddSingleton<ITorznabClient>(indexer).BuildServiceProvider();

    private sealed class ProgressWatchIndexer : ITorznabClient
    {
        public bool Block { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Quota { get; init; }
        public int Searches { get; private set; }
        public List<string> Phases { get; } = [];
        public Task CheckConnectionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<C411AccountStats> GetAccountStatsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(string query, IEnumerable<int>? categories = null,
            bool tvSearch = false, bool forceRefresh = false, int? tmdbId = null, int? seasonNumber = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReleaseCandidate>>([WatchIndexer.Candidate("Old")]);
        public async Task<IReadOnlyList<ReleaseCandidate>> SearchForWatchAsync(MediaItem media, CancellationToken ct = default, Func<string, Task>? progress = null)
        {
            Searches++;
            foreach (var phase in new[] { "rate-limit", "request" })
            {
                Phases.Add(phase);
                if (progress is not null) await progress(phase);
            }
            if (Quota) throw new HttpRequestException("quota", null, System.Net.HttpStatusCode.TooManyRequests);
            if (Block)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return [WatchIndexer.Candidate("Old")];
        }
    }

    [WatchDatabaseFact]
    public async Task ManualScanRequestsAreDeduplicatedAndRespectPausedMedia()
    {
        await using var database = await WatchDatabase.CreateAsync();
        using var first = database.Open();
        using var second = database.Open();
        var results = await Task.WhenAll(new WatchScanService(first).RequestAsync(true, default),
            new WatchScanService(second).RequestAsync(true, default));
        Assert.Equal(results[0]!.Id, results[1]!.Id);
        using var verify = database.Open();
        Assert.Equal(1, await verify.WatchScans.CountAsync());
        Assert.Equal(1, await verify.WatchScanItems.CountAsync());
        await verify.WatchScans.ExecuteUpdateAsync(update => update.SetProperty(value => value.State, "completed"));
        await new ReleaseWatchService(verify, new WatchIndexer()).SetEnabledAsync(database.MediaId, false, default);
        Assert.Null(await new WatchScanService(verify).RequestAsync(true, default));
        await new ReleaseWatchService(verify, new WatchIndexer()).SetEnabledAsync(database.MediaId, true, default);
        (await verify.WatchSettings.SingleAsync()).Enabled = false;
        await verify.SaveChangesAsync();
        Assert.Null(await new WatchScanService(verify).RequestAsync(true, default));
    }

    [WatchDatabaseFact]
    public async Task ManualPausePersistsAndResumeKeepsBaseline()
    {
        await using var database = await WatchDatabase.CreateAsync();
        using var db = database.Open();
        var indexer = new WatchIndexer();
        var service = new ReleaseWatchService(db, indexer);
        await service.ScanAsync(database.MediaId, default);
        await service.SetEnabledAsync(database.MediaId, false, default);
        using var reopened = database.Open();
        var resumed = new ReleaseWatchService(reopened, indexer);
        Assert.False((await resumed.StatusAsync(database.MediaId, default)).Enabled);
        Assert.Equal("disabled", (await resumed.StatusAsync(database.MediaId, default)).State);
        await resumed.ScanAsync(database.MediaId, default);
        Assert.Equal(1, indexer.Searches);
        await resumed.SetEnabledAsync(database.MediaId, true, default);
        Assert.Equal("active", (await resumed.StatusAsync(database.MediaId, default)).State);
        Assert.NotNull((await reopened.MediaWatches.SingleAsync()).InitializedAt);
    }

    [WatchDatabaseFact]
    public async Task MediaWatchStatusReflectsExclusionGlobalPauseInitializationAndErrors()
    {
        await using var database = await WatchDatabase.CreateAsync();
        using var db = database.Open();
        var indexer = new WatchIndexer();
        var service = new ReleaseWatchService(db, indexer);
        Assert.Equal("initializing", (await service.StatusAsync(database.MediaId, default)).State);
        await service.ScanAsync(database.MediaId, default);
        var active = await service.StatusAsync(database.MediaId, default);
        Assert.Equal("active", active.State);
        Assert.NotNull(active.LastCheckedAt);
        Assert.NotNull(active.NextCheckAt);
        (await db.MediaWatches.SingleAsync()).LastError = "Recherche echouee";
        await db.SaveChangesAsync();
        Assert.Equal("error", (await service.StatusAsync(database.MediaId, default)).State);
        (await db.WatchSettings.SingleAsync()).Enabled = false;
        await db.SaveChangesAsync();
        var paused = await service.StatusAsync(database.MediaId, default);
        Assert.Equal("disabled", paused.State);
        Assert.Null(paused.NextCheckAt);
        (await db.MediaItems.SingleAsync()).StoragePath = "/library/Alien";
        await db.SaveChangesAsync();
        Assert.Equal("excluded", (await service.StatusAsync(database.MediaId, default)).State);
    }

    [WatchDatabaseFact]
    public async Task StockMoviesAreExcludedButMissingMoviesAndStockSeriesRemainWatched()
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new WatchIndexer();
        using var db = database.Open();
        var movie = await db.MediaItems.SingleAsync();
        movie.StoragePath = "/library/Alien";
        await db.SaveChangesAsync();
        await new ReleaseWatchService(db, indexer).ScanAsync(movie.Id, default);
        Assert.Equal(0, indexer.Searches);
        Assert.Empty(await db.ReleaseObservations.ToListAsync());
        movie.StoragePath = " ";
        db.CloudFiles.Add(new CloudFile { MediaItemId = movie.Id, Path = "/Films/Alien.mkv" });
        await db.SaveChangesAsync();
        Assert.False(await ReleaseWatchService.WatchedMedia(db).AnyAsync());
        await new ReleaseWatchService(db, indexer).ScanAsync(movie.Id, default);
        Assert.Equal(0, indexer.Searches);
        var series = new MediaItem { Title = "Series", Type = MediaType.Series, StoragePath = "/library/Series" };
        db.MediaItems.Add(series);
        await db.SaveChangesAsync();
        Assert.Equal(series.Id, (await ReleaseWatchService.WatchedMedia(db).SingleAsync()).Id);
        db.CloudFiles.RemoveRange(await db.CloudFiles.ToListAsync());
        await db.SaveChangesAsync();
        Assert.Equal(2, await ReleaseWatchService.WatchedMedia(db).CountAsync());
        await new ReleaseWatchService(db, indexer).ScanAsync(movie.Id, default);
        Assert.Equal(1, indexer.Searches);
    }

    [WatchDatabaseFact]
    public async Task BaselineIsSilentAndBlacklistSurvivesRescans()
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new WatchIndexer();
        using var db = database.Open();
        var service = new ReleaseWatchService(db, indexer);
        await service.ScanAsync(database.MediaId, default);
        Assert.All(await db.ReleaseObservations.ToListAsync(), value => Assert.Equal(ReleaseDecision.Baseline, value.Decision));
        Assert.Empty(await db.PipelineJobs.ToListAsync());
        indexer.Releases.Add(WatchIndexer.Candidate("New"));
        await service.ScanAsync(database.MediaId, default);
        var proposal = await db.ReleaseObservations.SingleAsync(value => value.Decision == ReleaseDecision.Pending);
        await service.DecideAsync(proposal.Id, false, default);
        db.ChangeTracker.Clear();
        await service.ScanAsync(database.MediaId, default);
        Assert.Equal(2, await db.ReleaseObservations.CountAsync());
        Assert.Equal(ReleaseDecision.Blacklisted, (await db.ReleaseObservations.FindAsync(proposal.Id))!.Decision);
        Assert.Empty(await db.PipelineJobs.ToListAsync());
    }

    [WatchDatabaseFact]
    public async Task ConcurrentAcceptsCreateOnlyOneDownloadJob()
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new WatchIndexer();
        var id = await database.ProposeAsync(indexer);
        using var first = database.Open();
        using var second = database.Open();
        await Task.WhenAll(new ReleaseWatchService(first, indexer).DecideAsync(id, true, default),
            new ReleaseWatchService(second, indexer).DecideAsync(id, true, default));
        using var verify = database.Open();
        Assert.Equal(1, await verify.PipelineJobs.CountAsync());
        var observation = await verify.ReleaseObservations.FindAsync(id);
        Assert.Equal(ReleaseDecision.Accepted, observation!.Decision);
        Assert.Equal(observation.JobId, (await verify.PipelineJobs.SingleAsync()).Id);
    }

    [WatchDatabaseFact]
    public async Task QualityPassingReleaseWithoutLinkIsProposedAndQualityFailuresAreFiltered()
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new WatchIndexer();
        indexer.Releases[0] = WatchIndexer.Candidate("Old", downloadUrl: null);
        using var db = database.Open();
        var service = new ReleaseWatchService(db, indexer);
        await service.ScanAsync(database.MediaId, default);
        Assert.Equal(ReleaseDecision.Baseline, (await db.ReleaseObservations.SingleAsync()).Decision);
        indexer.Releases.Add(WatchIndexer.Candidate("New", downloadUrl: null));
        indexer.Releases.Add(WatchIndexer.Candidate("NoSeeders", 0, downloadUrl: null));
        var result = await service.ScanAsync(database.MediaId, default);
        Assert.Equal(1, result!.Filtered);
        Assert.Equal(1, result.NewProposals);
        var pending = await db.ReleaseObservations.SingleAsync(value => value.Decision == ReleaseDecision.Pending);
        Assert.EndsWith("-New", pending.Title);
        Assert.Equal(string.Empty, pending.DownloadUrl);
        Assert.Equal(1, await db.ReleaseObservations.CountAsync(value => value.Decision == ReleaseDecision.Filtered));
        await service.ScanAsync(database.MediaId, default);
        Assert.Equal(1, await db.ReleaseObservations.CountAsync(value => value.Decision == ReleaseDecision.Pending));
        Assert.Empty(await db.PipelineJobs.ToListAsync());
    }

    [WatchDatabaseFact]
    public async Task AcceptResolvesLinkForExactQualityPassingRelease()
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new WatchIndexer();
        var id = await database.ProposeAsync(indexer);
        indexer.WatchReleases = [WatchIndexer.Candidate("Old", downloadUrl: null), WatchIndexer.Candidate("New", downloadUrl: null)];
        using var db = database.Open();
        var result = await new ReleaseWatchService(db, indexer).DecideAsync(id, true, default);
        Assert.StartsWith("Accepte", result);
        var job = await db.PipelineJobs.SingleAsync();
        var payload = System.Text.Json.JsonSerializer.Deserialize<ReleasePayload>(job.PayloadJson!)!;
        Assert.Equal(indexer.Releases[1].DownloadUrl, payload.DownloadUrl);
        Assert.Equal(indexer.Releases[1].Title, payload.Title);
        Assert.Equal(4, indexer.Searches);
    }

    [WatchDatabaseTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptWithoutMatchingDownloadLinkLeavesProposalPending(bool differentRelease)
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new WatchIndexer();
        var id = await database.ProposeAsync(indexer);
        indexer.WatchReleases = [WatchIndexer.Candidate("New", downloadUrl: null)];
        indexer.Releases.Clear();
        indexer.Releases.Add(WatchIndexer.Candidate(differentRelease ? "Different" : "New", downloadUrl: differentRelease ? "https://example.test/other" : null));
        using var db = database.Open();
        var result = await new ReleaseWatchService(db, indexer).DecideAsync(id, true, default);
        Assert.Contains("Lien de telechargement indisponible", result);
        Assert.Equal(ReleaseDecision.Pending, (await db.ReleaseObservations.SingleAsync(value => value.Id == id)).Decision);
        Assert.Empty(await db.PipelineJobs.ToListAsync());
    }

    [WatchDatabaseFact]
    public async Task MediaDetailAndWatchUseSameAssignedQualityProfile()
    {
        await using var database = await WatchDatabase.CreateAsync();
        using var db = database.Open();
        var profile = new ReleaseProfile { Name = "Assigned profile", MinSeeders = 20 };
        db.ReleaseProfiles.Add(profile);
        (await db.MediaItems.SingleAsync()).ReleaseProfileId = profile.Id;
        await db.SaveChangesAsync();
        var indexer = new WatchIndexer();
        var service = new ReleaseWatchService(db, indexer);
        await service.ScanAsync(database.MediaId, default);
        indexer.Releases.Add(WatchIndexer.Candidate("New", downloadUrl: null));
        await service.ScanAsync(database.MediaId, default);
        Assert.Equal(1, await db.ReleaseObservations.CountAsync(value => value.Decision == ReleaseDecision.Filtered));
        var controller = new MediaController(db, null!, null!, indexer, null!, null!, null!, null!, service);
        var response = (await controller.Releases(database.MediaId, null, default)).Value!;
        Assert.All(response.Releases, release => Assert.False(release.Accepted));
    }

    [WatchDatabaseFact]
    public async Task PreviouslyFilteredQualityPassingReleaseWithoutLinkIsProposed()
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new WatchIndexer();
        var id = await database.ProposeAsync(indexer);
        using var db = database.Open();
        await db.ReleaseObservations.Where(value => value.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(value => value.Decision, ReleaseDecision.Filtered).SetProperty(value => value.DownloadUrl, string.Empty));
        indexer.Releases[1] = WatchIndexer.Candidate("New", downloadUrl: null);
        await new ReleaseWatchService(db, indexer).ScanAsync(database.MediaId, default);
        Assert.Equal(ReleaseDecision.Pending, (await db.ReleaseObservations.SingleAsync(value => value.Id == id)).Decision);
        Assert.Empty(await db.PipelineJobs.ToListAsync());
    }

    [WatchDatabaseFact]
    public async Task NewFilteredReleaseCanBecomeAvailableLater()
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new WatchIndexer();
        using var db = database.Open();
        var service = new ReleaseWatchService(db, indexer);
        await service.ScanAsync(database.MediaId, default);
        indexer.Releases.Add(WatchIndexer.Candidate("New", 0));
        await service.ScanAsync(database.MediaId, default);
        Assert.Equal(1, await db.ReleaseObservations.CountAsync(value => value.Decision == ReleaseDecision.Filtered));
        indexer.Releases[1] = WatchIndexer.Candidate("New", 10);
        await service.ScanAsync(database.MediaId, default);
        Assert.Equal(1, await db.ReleaseObservations.CountAsync(value => value.Decision == ReleaseDecision.Pending));
        Assert.Equal(2, await db.ReleaseObservations.CountAsync());
        Assert.Empty(await db.PipelineJobs.ToListAsync());
    }

    [WatchDatabaseFact]
    public async Task ExpiredOrDisappearedReleaseNeverStartsPipeline()
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new WatchIndexer();
        var id = await database.ProposeAsync(indexer);
        using var db = database.Open();
        indexer.Releases.Clear();
        await new ReleaseWatchService(db, indexer).DecideAsync(id, true, default);
        Assert.Empty(await db.PipelineJobs.ToListAsync());
        await db.ReleaseObservations.Where(value => value.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await new ReleaseWatchService(db, indexer).DecideAsync(id, true, default);
        Assert.Equal(ReleaseDecision.Expired, (await db.ReleaseObservations.AsNoTracking().SingleAsync(value => value.Id == id)).Decision);
        Assert.Empty(await db.PipelineJobs.ToListAsync());
    }

    [WatchDatabaseFact]
    public async Task AcceptAndBlacklistRaceHasOneFinalDecision()
    {
        await using var database = await WatchDatabase.CreateAsync();
        var indexer = new WatchIndexer();
        var id = await database.ProposeAsync(indexer);
        using var first = database.Open();
        using var second = database.Open();
        await Task.WhenAll(new ReleaseWatchService(first, indexer).DecideAsync(id, true, default),
            new ReleaseWatchService(second, indexer).DecideAsync(id, false, default));
        using var verify = database.Open();
        var observation = await verify.ReleaseObservations.FindAsync(id);
        Assert.Contains(observation!.Decision, new[] { ReleaseDecision.Accepted, ReleaseDecision.Blacklisted });
        Assert.Equal(observation.Decision == ReleaseDecision.Accepted ? 1 : 0, await verify.PipelineJobs.CountAsync());
    }

    private class WatchDatabaseFactAttribute : FactAttribute
    {
        public WatchDatabaseFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEDIAHUB_TEST_DB")))
                Skip = "Set MEDIAHUB_TEST_DB to a PostgreSQL connection permitted to create isolated test databases.";
        }
    }

    private class WatchDatabaseTheoryAttribute : TheoryAttribute
    {
        public WatchDatabaseTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEDIAHUB_TEST_DB")))
                Skip = "Set MEDIAHUB_TEST_DB to a PostgreSQL connection permitted to create isolated test databases.";
        }
    }

    private sealed class WatchDatabase(string connection) : IAsyncDisposable
    {
        public Guid MediaId { get; } = Guid.NewGuid();
        public AppDbContext Open() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        public static async Task<WatchDatabase> CreateAsync()
        {
            var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MEDIAHUB_TEST_DB"))
                { Database = "mediahub_watch_tests_" + Guid.NewGuid().ToString("N") };
            var database = new WatchDatabase(builder.ConnectionString);
            using var db = database.Open();
            await db.Database.EnsureCreatedAsync();
            (await db.WatchSettings.SingleAsync()).Enabled = true;
            db.MediaItems.Add(new MediaItem { Id = database.MediaId, Title = "Alien", Year = 1979, Type = MediaType.Movie });
            db.MediaWatches.Add(new MediaWatch { MediaItemId = database.MediaId });
            await db.SaveChangesAsync();
            return database;
        }
        public async Task<Guid> ProposeAsync(WatchIndexer indexer)
        {
            using var db = Open();
            var service = new ReleaseWatchService(db, indexer);
            await service.ScanAsync(MediaId, default);
            indexer.Releases.Add(WatchIndexer.Candidate("New"));
            await service.ScanAsync(MediaId, default);
            return (await db.ReleaseObservations.SingleAsync(value => value.Decision == ReleaseDecision.Pending)).Id;
        }
        public async ValueTask DisposeAsync()
        {
            using var db = Open();
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class WatchIndexer : ITorznabClient
    {
        public int Searches { get; private set; }
        public bool Failure { get; set; }
        public List<ReleaseCandidate> Releases { get; } = [Candidate("Old")];
        public IReadOnlyList<ReleaseCandidate>? WatchReleases { get; set; }
        public static ReleaseCandidate Candidate(string group, int seeders = 10, string? downloadUrl = "https://example.test/download")
        {
            var candidate = new ReleaseCandidate { Title = $"Alien.1979.MULTi.1080p.WEB-DL.x264-{group}",
            SizeBytes = 100, Seeders = seeders, DownloadUrl = downloadUrl };
            ReleaseParser.Parse(candidate);
            return candidate;
        }
        public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(string query, IEnumerable<int>? categories = null,
            bool tvSearch = false, bool forceRefresh = false, int? tmdbId = null, int? seasonNumber = null, CancellationToken ct = default) =>
            Search();
        public Task<IReadOnlyList<ReleaseCandidate>> SearchForWatchAsync(MediaItem media, CancellationToken ct = default, Func<string, Task>? progress = null) =>
            Search(true);
        private Task<IReadOnlyList<ReleaseCandidate>> Search(bool watch = false)
        {
            Searches++;
            if (Failure) throw new HttpRequestException("indexer unavailable");
            return Task.FromResult<IReadOnlyList<ReleaseCandidate>>((watch ? WatchReleases : null) ?? Releases.ToList());
        }
        public Task CheckConnectionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<C411AccountStats> GetAccountStatsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}