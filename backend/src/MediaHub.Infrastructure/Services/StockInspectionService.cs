using System.Text.Json;
using MediaHub.Core.Entities;
using MediaHub.Infrastructure.Data;
using MediaHub.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaHub.Infrastructure.Services;

public class StockInspectionService(AppDbContext db, StorageMediaInspector inspector, IOptions<LibraryOptions> library)
{
    public async Task<StockInspection?> RequestAsync(Guid mediaId, int? maxFiles, bool force, Guid? fileId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8112302)", ct);
        var media = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(value => value.Id == mediaId, ct);
        if (media is null || string.IsNullOrWhiteSpace(media.StoragePath)) return null;
        if (fileId is { } selected && !await db.StockFileInspections.AnyAsync(value => value.Id == selected && value.MediaItemId == mediaId && value.IsPresent, ct)) return null;
        var scan = await db.StockInspections.FirstOrDefaultAsync(value => value.MediaItemId == mediaId, ct);
        if (scan?.Status is "Queued" or "Running") return scan;
        if (scan is null)
        {
            scan = new StockInspection { MediaItemId = mediaId };
            db.StockInspections.Add(scan);
        }
        scan.Generation = Guid.NewGuid();
        scan.Status = "Queued";
        scan.RequestedAt = DateTimeOffset.UtcNow;
        scan.FinishedAt = null;
        scan.Error = null;
        scan.CurrentFile = null;
        scan.TotalFiles = scan.ProcessedFiles = scan.InspectedFiles = scan.FailedFiles = 0;
        scan.MaxFiles = maxFiles;
        scan.Force = force;
        scan.SelectedFileId = fileId;
        scan.FileIdsJson = "[]";
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return scan;
    }

    public Task<int> CancelAsync(Guid mediaId, CancellationToken ct) => db.StockInspections
        .Where(value => value.MediaItemId == mediaId && (value.Status == "Queued" || value.Status == "Running"))
        .ExecuteUpdateAsync(update => update.SetProperty(value => value.Status, "Cancelled")
            .SetProperty(value => value.CurrentFile, (string?)null).SetProperty(value => value.FinishedAt, DateTimeOffset.UtcNow), ct);

    public async Task ProcessNextAsync(CancellationToken ct)
    {
        var scan = await db.StockInspections.AsNoTracking().Where(value => value.Status == "Queued" || value.Status == "Running")
            .OrderBy(value => value.RequestedAt).FirstOrDefaultAsync(ct);
        if (scan is null) return;
        try
        {
            StorageAvailability.EnsureAvailable(library.Value);
            if (scan.Status == "Queued")
            {
                var media = await db.MediaItems.AsNoTracking().SingleAsync(value => value.Id == scan.MediaItemId, ct);
                if (string.IsNullOrWhiteSpace(media.StoragePath)) throw new DirectoryNotFoundException();
                var inventory = await inspector.ListAsync(media.StoragePath, ct);
                var cached = await db.StockFileInspections.Where(value => value.MediaItemId == scan.MediaItemId).ToListAsync(ct);
                var byPath = cached.ToDictionary(value => value.Path, StringComparer.Ordinal);
                foreach (var file in cached) file.IsPresent = false;
                var selected = new List<StockFileInspection>();
                foreach (var file in inventory.OrderBy(value => value.Path, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(file.Path), TimeSpan.Zero);
                    if (!byPath.TryGetValue(file.Path, out var record))
                    {
                        record = new StockFileInspection { MediaItemId = scan.MediaItemId, Path = file.Path };
                        db.StockFileInspections.Add(record);
                    }
                    if (record.SizeBytes != file.SizeBytes || record.ModifiedAt.ToUnixTimeMilliseconds() != modified.ToUnixTimeMilliseconds()) record.Status = "Pending";
                    record.SizeBytes = file.SizeBytes;
                    record.ModifiedAt = modified;
                    record.IsPresent = true;
                    if (scan.SelectedFileId is { } fileId && record.Id != fileId) continue;
                    if (scan.Force || record.Status != "Ready") selected.Add(record);
                }
                var todo = selected.Take(scan.MaxFiles ?? int.MaxValue).Select(value => value.Id).ToList();
                await db.SaveChangesAsync(ct);
                await Active(scan).ExecuteUpdateAsync(update => update.SetProperty(value => value.FileIdsJson, JsonSerializer.Serialize(todo))
                    .SetProperty(value => value.TotalFiles, todo.Count).SetProperty(value => value.Status, todo.Count == 0 ? "Completed" : "Running")
                    .SetProperty(value => value.FinishedAt, todo.Count == 0 ? DateTimeOffset.UtcNow : (DateTimeOffset?)null), ct);
                return;
            }
            var ids = JsonSerializer.Deserialize<List<Guid>>(scan.FileIdsJson)!;
            if (scan.ProcessedFiles >= ids.Count)
            {
                await FinishAsync(scan, null, ct);
                return;
            }
            var current = await db.StockFileInspections.SingleAsync(value => value.Id == ids[scan.ProcessedFiles], ct);
            if (await Active(scan).ExecuteUpdateAsync(update => update.SetProperty(value => value.CurrentFile, Path.GetFileName(current.Path)), ct) == 0) return;
            var failed = false;
            try
            {
                var before = new FileInfo(current.Path);
                var size = before.Length;
                var modified = before.LastWriteTimeUtc;
                var details = (await inspector.InspectAsync(current.Path, ct, TimeSpan.FromSeconds(30))).Single();
                var after = new FileInfo(current.Path);
                if (after.Length != size || after.LastWriteTimeUtc != modified) throw new IOException();
                var stem = Path.GetFileNameWithoutExtension(current.Path);
                var external = Directory.EnumerateFiles(Path.GetDirectoryName(current.Path)!).Where(path =>
                    Path.GetFileNameWithoutExtension(path).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.OrdinalIgnoreCase))
                    .Where(path => new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
                    .Select(Path.GetFileName).OfType<string>().Order().ToArray();
                current.DetailsJson = JsonSerializer.Serialize(details with { Path = Path.GetFileName(details.Path), ExternalSubtitles = external });
                current.SizeBytes = size;
                current.ModifiedAt = new DateTimeOffset(modified, TimeSpan.Zero);
                current.InspectedAt = DateTimeOffset.UtcNow;
                current.Status = "Ready";
                current.Error = null;
            }
            catch (Exception exception) when (!ct.IsCancellationRequested && exception is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException or JsonException)
            {
                current.Status = "Failed";
                current.Error = exception is TimeoutException ? "Delai d'analyse depasse (30 s)." : "Fichier inaccessible ou analyse impossible.";
                failed = true;
            }
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await Active(scan).ExecuteUpdateAsync(update => update.SetProperty(value => value.ProcessedFiles, value => value.ProcessedFiles + 1)
                .SetProperty(value => value.InspectedFiles, value => value.InspectedFiles + (failed ? 0 : 1))
                .SetProperty(value => value.FailedFiles, value => value.FailedFiles + (failed ? 1 : 0))
                .SetProperty(value => value.CurrentFile, (string?)null), ct) == 0) return;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            await FinishAsync(scan, exception is DirectoryNotFoundException or FileNotFoundException
                ? "Le stockage monte ou le dossier du media est inaccessible." : "L'analyse du stock a echoue. Les resultats precedents sont conserves.", ct);
        }
    }

    private IQueryable<StockInspection> Active(StockInspection scan) => db.StockInspections.Where(value => value.MediaItemId == scan.MediaItemId &&
        value.Generation == scan.Generation && (value.Status == "Queued" || value.Status == "Running"));

    private Task<int> FinishAsync(StockInspection scan, string? error, CancellationToken ct) => Active(scan)
        .ExecuteUpdateAsync(update => update.SetProperty(value => value.Status, error == null ? "Completed" : "Failed")
            .SetProperty(value => value.Error, error).SetProperty(value => value.CurrentFile, (string?)null)
            .SetProperty(value => value.FinishedAt, DateTimeOffset.UtcNow), ct);
}