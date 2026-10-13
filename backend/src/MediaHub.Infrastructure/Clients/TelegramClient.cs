using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaHub.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace MediaHub.Infrastructure.Clients;

public class TelegramClient(HttpClient http, IOptions<TelegramOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly TelegramOptions settings = options.Value;

    public async Task CheckConnectionAsync(CancellationToken ct = default)
    {
        var bot = await CallAsync("getMe", new { }, ct);
        if (!bot.TryGetProperty("is_bot", out var isBot) || isBot.ValueKind != JsonValueKind.True ||
            !bot.TryGetProperty("id", out var id) || !id.TryGetInt64(out var botId) || botId <= 0)
            throw new InvalidOperationException("Telegram a renvoye une identite de bot invalide.");
    }

    public Task<JsonElement> UpdatesAsync(long offset, CancellationToken ct) =>
        CallAsync("getUpdates", new { offset, timeout = 20, allowed_updates = new[] { "callback_query", "message" } }, ct);

    public Task<JsonElement> SendAsync(string text, object? keyboard, CancellationToken ct) =>
        CallAsync("sendMessage", new { chat_id = settings.ChatId, text, reply_markup = keyboard }, ct);

    public Task<JsonElement> AnswerAsync(string callbackId, string text, CancellationToken ct) =>
        CallAsync("answerCallbackQuery", new { callback_query_id = callbackId, text = text.Length > 180 ? text[..180] : text }, ct);

    public Task<JsonElement> EditAsync(long messageId, string text, CancellationToken ct, object? keyboard = null) =>
        CallAsync("editMessageText", new { chat_id = settings.ChatId, message_id = messageId, text, reply_markup = keyboard ?? new { inline_keyboard = Array.Empty<object[]>() } }, ct);

    private async Task<JsonElement> CallAsync(string method, object body, CancellationToken ct)
    {
        if (!settings.Configured) throw new InvalidOperationException("Telegram non configure.");
        using var response = await http.PostAsJsonAsync($"./bot{settings.BotToken}/{method}", body, JsonOptions, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Telegram HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!document.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Telegram a refuse la requete.");
        return document.RootElement.GetProperty("result").Clone();
    }
}