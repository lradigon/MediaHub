using MediaHub.Core.Entities;
using MediaHub.Infrastructure.Clients;
using MediaHub.Infrastructure.Data;
using MediaHub.Infrastructure.Options;
using MediaHub.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaHub.Worker;

public class TelegramWorker(IServiceScopeFactory scopes, IOptions<TelegramOptions> options, ILogger<TelegramWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) => options.Value.Configured
        ? Task.WhenAll(PollAsync(ct), NotifyAsync(ct)) : Task.CompletedTask;

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var telegram = scope.ServiceProvider.GetRequiredService<TelegramClient>();
                var settings = await db.WatchSettings.SingleAsync(ct);
                var updates = await telegram.UpdatesAsync(settings.TelegramOffset, ct);
                foreach (var update in updates.EnumerateArray())
                {
                    if (update.TryGetProperty("callback_query", out var callback))
                    {
                        var callbackId = callback.GetProperty("id").GetString()!;
                        var authorized = callback.TryGetProperty("message", out var message) && options.Value.IsAuthorized(
                            callback.GetProperty("from").GetProperty("id").GetInt64(), message.GetProperty("chat").GetProperty("id").GetInt64());
                        var result = "Decision non autorisee.";
                        var data = callback.TryGetProperty("data", out var content) ? content.GetString() : null;
                        if (authorized && TelegramTrackSelectionService.TryParseCallback(data, out var sessionId, out var action))
                        {
                            try
                            {
                                result = await scope.ServiceProvider.GetRequiredService<TelegramTrackSelectionService>()
                                    .HandleAsync(sessionId, message.GetProperty("message_id").GetInt64(), action, ct);
                            }
                            catch (Exception exception) when (!ct.IsCancellationRequested)
                            {
                                logger.LogWarning("Telegram track selection failed ({ErrorType})", exception.GetType().Name);
                                result = "Selection indisponible. Reessaie plus tard ou utilise MediaHub.";
                            }
                        }
                        else if (authorized && data is { Length: 34 } && (data.StartsWith("a:") || data.StartsWith("b:")) &&
                            Guid.TryParseExact(data[2..], "N", out var id))
                        {
                            result = await scope.ServiceProvider.GetRequiredService<ReleaseWatchService>().DecideAsync(id, data[0] == 'a', ct);
                            var observation = await db.ReleaseObservations.AsNoTracking().FirstOrDefaultAsync(value => value.Id == id, ct);
                            if (observation is not null && observation.Decision != ReleaseDecision.Pending)
                            {
                                try { await telegram.EditAsync(callback.GetProperty("message").GetProperty("message_id").GetInt64(), $"{observation.Title}\n{result}", ct); }
                                catch (Exception) when (!ct.IsCancellationRequested) { logger.LogDebug("Telegram message edit unavailable"); }
                            }
                        }
                        try { await telegram.AnswerAsync(callbackId, result, ct); }
                        catch (Exception) when (!ct.IsCancellationRequested) { logger.LogDebug("Telegram callback acknowledgement unavailable"); }
                    }
                    else if (update.TryGetProperty("message", out var incoming) &&
                        incoming.TryGetProperty("from", out var sender) && sender.GetProperty("id").GetInt64() == options.Value.ChatId &&
                        incoming.GetProperty("chat").GetProperty("id").GetInt64() == options.Value.ChatId &&
                        incoming.TryGetProperty("text", out var text) && text.GetString() == "/start")
                        await telegram.SendAsync("MediaHub connecte. Les nouvelles releases attendront votre decision.", null, ct);
                    settings.TelegramOffset = update.GetProperty("update_id").GetInt64() + 1;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Telegram polling failed ({ErrorType})", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    private async Task NotifyAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var telegram = scope.ServiceProvider.GetRequiredService<TelegramClient>();
                try { await scope.ServiceProvider.GetRequiredService<TelegramTrackSelectionService>().NotifyNextAsync(ct); }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Telegram track notification failed ({ErrorType})", exception.GetType().Name);
                }
                var now = DateTimeOffset.UtcNow;
                var eligible = ReleaseWatchService.WatchedMedia(db).Select(value => value.Id);
                var pending = await db.ReleaseObservations.Include(value => value.MediaItem)
                    .Where(value => value.Decision == ReleaseDecision.Pending && value.NotifiedAt == null && value.ExpiresAt > now && value.NextNotificationAt <= now && eligible.Contains(value.MediaItemId))
                    .OrderBy(value => value.FirstSeenAt).Take(10).ToListAsync(ct);
                foreach (var observation in pending)
                {
                    try
                    {
                        var keyboard = new { inline_keyboard = new[] { new[] {
                            new { text = "Accepter", callback_data = $"a:{observation.Id:N}" },
                            new { text = "Blacklister", callback_data = $"b:{observation.Id:N}" }
                        } } };
                        var title = observation.Title.Length > 1200 ? observation.Title[..1200] : observation.Title;
                        await telegram.SendAsync($"{observation.MediaItem?.Title}\n{title}\n{observation.Coverage} - {observation.SizeBytes / 1073741824d:F2} Gio\nDecision avant {observation.ExpiresAt:dd/MM HH:mm} UTC", keyboard, ct);
                        observation.NotifiedAt = DateTimeOffset.UtcNow;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        observation.NotificationAttempts++;
                        observation.NextNotificationAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(7, observation.NotificationAttempts))));
                        logger.LogWarning("Telegram notification failed ({ErrorType})", ex.GetType().Name);
                    }
                    await db.SaveChangesAsync(ct);
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Telegram outbox failed ({ErrorType})", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }
}