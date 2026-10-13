using System.Text.Json;
using MediaHub.Core;
using MediaHub.Core.Entities;
using MediaHub.Infrastructure.Clients;
using MediaHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaHub.Infrastructure.Services;

public record TelegramSelectionMessage(string Text, object Keyboard);

public class TelegramTrackSelectionService(AppDbContext db, TrackSelectionService selections, TelegramClient telegram)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const int PageSize = 8;

    public async Task NotifyNextAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var session = await db.TelegramTrackSelections.SingleAsync(ct);
            await db.Entry(session).ReloadAsync(ct);
            if (session.JobId is { } active)
            {
                if (await WaitingAsync(active, ct))
                {
                    if (session.MessageId is null) await SendAsync(session, ct);
                    return;
                }
                await RetireAsync(session, "Selection terminee ou annulee depuis MediaHub.", ct);
            }
            var next = await db.PipelineJobs.AsNoTracking().Where(job => job.Kind == JobKind.TrackSelection && job.Status == JobStatus.WaitingForSelection)
                .OrderBy(job => job.CreatedAt).Select(job => (Guid?)job.Id).FirstOrDefaultAsync(ct);
            if (next is null) return;
            var details = await selections.GetAsync(next.Value, ct);
            if (details is null) return;
            session.SessionId = Guid.NewGuid();
            session.JobId = next;
            SetDetails(session, details);
            await db.SaveChangesAsync(ct);
            if (!await WaitingAsync(next.Value, ct))
            {
                await RetireAsync(session, "Selection deja terminee.", ct);
                return;
            }
            await SendAsync(session, ct);
        }
        finally { Gate.Release(); }
    }

    public async Task<string> HandleAsync(Guid sessionId, long messageId, string action, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var session = await db.TelegramTrackSelections.SingleAsync(ct);
            await db.Entry(session).ReloadAsync(ct);
            if (session.SessionId != sessionId || session.MessageId != messageId || session.JobId is not { } jobId)
                return "Cette demande n'est plus active.";
            if (!await WaitingAsync(jobId, ct))
            {
                await RetireAsync(session, "Selection terminee ou annulee depuis MediaHub.", ct);
                return "Ce choix a deja ete traite.";
            }
            var file = JsonSerializer.Deserialize<StockMediaFile>(session.FileJson!)!;
            var audio = JsonSerializer.Deserialize<List<int>>(session.AudioIndexesJson)!;
            var subtitles = JsonSerializer.Deserialize<List<int>>(session.SubtitleIndexesJson)!;
            if (action is "c" or "g")
            {
                var result = await selections.ConfirmAsync(jobId, audio, subtitles, action == "g", ct, TrackSelectionService.LayoutSignature(file));
                if (result.Error is not null) return result.Error;
                await RetireAsync(session, $"Selection confirmee pour {result.AppliedCount} fichier(s).", ct);
                return $"{result.AppliedCount} fichier(s) valide(s).";
            }
            if (action == "r")
            {
                var details = await selections.GetAsync(jobId, ct);
                if (details is null) return "Ce choix a deja ete traite.";
                SetDetails(session, details);
            }
            else if (action.Length > 1 && int.TryParse(action[1..], out var index))
            {
                if (action[0] == 'p') session.Page = Math.Clamp(index, 0, Math.Max(0, (file.AudioTracks.Count + file.SubtitleTracks.Count - 1) / PageSize));
                else if (action[0] == 'a' && file.AudioTracks.Any(track => track.Index == index && !track.IsAudioDescription)) Toggle(audio, index);
                else if (action[0] == 's' && file.SubtitleTracks.Any(track => track.Index == index && !TrackSelectionPolicy.IsSdh(track))) Toggle(subtitles, index);
                else return "Choix invalide ou piste interdite.";
                session.AudioIndexesJson = JsonSerializer.Serialize(audio);
                session.SubtitleIndexesJson = JsonSerializer.Serialize(subtitles);
            }
            else return "Choix invalide.";
            await db.SaveChangesAsync(ct);
            var message = Render(session);
            try { await telegram.EditAsync(messageId, message.Text, ct, message.Keyboard); }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.BadRequest) { }
            return "Selection mise a jour.";
        }
        finally { Gate.Release(); }
    }

    public static bool TryParseCallback(string? data, out Guid session, out string action)
    {
        session = default;
        action = string.Empty;
        if (data is null || data.Length > 64) return false;
        var parts = data.Split(':');
        if (parts.Length != 3 || parts[0] != "t" || !Guid.TryParseExact(parts[1], "N", out session)) return false;
        action = parts[2];
        return action is "c" or "g" or "r" || (action.Length is > 1 and <= 8 && "asp".Contains(action[0]) &&
            int.TryParse(action[1..], out var index) && index >= 0);
    }

    public static TelegramSelectionMessage Render(TelegramTrackSelection session)
    {
        var file = JsonSerializer.Deserialize<StockMediaFile>(session.FileJson!)!;
        var audio = JsonSerializer.Deserialize<List<int>>(session.AudioIndexesJson)!;
        var subtitles = JsonSerializer.Deserialize<List<int>>(session.SubtitleIndexesJson)!;
        var tracks = file.AudioTracks.Select(track => new
        {
            Label = $"Audio #{track.Index}: {track.Language ?? "?"} {track.Codec} {track.Channels} {track.Name}".Trim(),
            Action = $"a{track.Index}", Selected = audio.Contains(track.Index), Allowed = !track.IsAudioDescription
        }).Concat(file.SubtitleTracks.Select(track => new
        {
            Label = $"Sous-titre #{track.Index}: {track.Language ?? "?"} {track.Format} {track.Source}{(track.IsForced ? " force" : "")}{(track.IsDefault ? " defaut" : "")}".Trim(),
            Action = $"s{track.Index}", Selected = subtitles.Contains(track.Index), Allowed = !TrackSelectionPolicy.IsSdh(track)
        })).ToList();
        var pageCount = Math.Max(1, (tracks.Count + PageSize - 1) / PageSize);
        var page = Math.Clamp(session.Page, 0, pageCount - 1);
        var visible = tracks.Skip(page * PageSize).Take(PageSize).ToList();
        var text = $"Choix des pistes\n{session.Heading}\nAudio retenu : {string.Join(", ", audio.Select(index => "#" + index))}\nSous-titres retenus : {(subtitles.Count == 0 ? "aucun" : string.Join(", ", subtitles.Select(index => "#" + index)))}\nPage {page + 1}/{pageCount}\n\n" +
            string.Join("\n", visible.Select(track => $"{(track.Selected ? "[x]" : "[ ]")} {Shorten(track.Label, 220)}{(track.Allowed ? "" : " (exclue par les regles)")}"));
        var rows = visible.Where(track => track.Allowed).Select(track => new object[]
        {
            new { text = Shorten($"{(track.Selected ? "[x]" : "[ ]")} {track.Label}", 65), callback_data = $"t:{session.SessionId:N}:{track.Action}" }
        }).ToList();
        if (pageCount > 1)
        {
            var navigation = new List<object>();
            if (page > 0) navigation.Add(new { text = "Precedent", callback_data = $"t:{session.SessionId:N}:p{page - 1}" });
            if (page + 1 < pageCount) navigation.Add(new { text = "Suivant", callback_data = $"t:{session.SessionId:N}:p{page + 1}" });
            rows.Add(navigation.ToArray());
        }
        rows.Add([new { text = "Confirmer", callback_data = $"t:{session.SessionId:N}:c" }]);
        rows.Add([new { text = "Confirmer pour les pistes identiques", callback_data = $"t:{session.SessionId:N}:g" }]);
        rows.Add([new { text = "Actualiser", callback_data = $"t:{session.SessionId:N}:r" }]);
        return new(text, new { inline_keyboard = rows });
    }

    private Task<bool> WaitingAsync(Guid id, CancellationToken ct) => db.PipelineJobs.AsNoTracking()
        .AnyAsync(job => job.Id == id && job.Kind == JobKind.TrackSelection && job.Status == JobStatus.WaitingForSelection, ct);

    private async Task SendAsync(TelegramTrackSelection session, CancellationToken ct)
    {
        var message = Render(session);
        var sent = await telegram.SendAsync(message.Text, message.Keyboard, ct);
        session.MessageId = sent.GetProperty("message_id").GetInt64();
        await db.SaveChangesAsync(ct);
    }

    private async Task RetireAsync(TelegramTrackSelection session, string reason, CancellationToken ct)
    {
        var messageId = session.MessageId;
        var heading = session.Heading;
        session.JobId = null;
        session.MessageId = null;
        session.FileJson = null;
        session.SessionId = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        if (messageId is not { } id) return;
        try { await telegram.EditAsync(id, $"{heading}\n{reason}", ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { }
    }

    private static void SetDetails(TelegramTrackSelection session, TrackSelectionDetails details)
    {
        session.FileJson = JsonSerializer.Serialize(details.File);
        session.AudioIndexesJson = JsonSerializer.Serialize(details.Recommendation.AudioTrackIndexes);
        session.SubtitleIndexesJson = JsonSerializer.Serialize(details.Recommendation.SubtitleTrackIndexes);
        session.Heading = $"{Shorten(details.Job.MediaItem?.Title ?? "Media", 180)}\n{Shorten(Path.GetFileName(details.File.Path), 220)}\n{Shorten(details.Job.Error ?? string.Empty, 180)}";
        session.Page = 0;
    }

    private static void Toggle(List<int> indexes, int index)
    {
        if (!indexes.Remove(index)) indexes.Add(index);
        indexes.Sort();
    }

    private static string Shorten(string value, int limit) => value.Length > limit ? value[..limit] + "..." : value;
}