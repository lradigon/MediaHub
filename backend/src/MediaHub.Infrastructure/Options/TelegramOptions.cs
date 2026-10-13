namespace MediaHub.Infrastructure.Options;

public class TelegramOptions
{
    public string BotToken { get; set; } = string.Empty;
    public long ChatId { get; set; }
    public bool Configured => !string.IsNullOrWhiteSpace(BotToken) && ChatId > 0;
    public bool IsAuthorized(long senderId, long chatId) => Configured && senderId == ChatId && chatId == ChatId;
}