namespace MediaHub.Core.Entities;

public class TelegramTrackSelection
{
    public int Id { get; set; } = 1;
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public Guid? JobId { get; set; }
    public long? MessageId { get; set; }
    public string? FileJson { get; set; }
    public string AudioIndexesJson { get; set; } = "[]";
    public string SubtitleIndexesJson { get; set; } = "[]";
    public string? Heading { get; set; }
    public int Page { get; set; }
}