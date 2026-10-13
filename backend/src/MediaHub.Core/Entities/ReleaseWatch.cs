namespace MediaHub.Core.Entities;

public enum ReleaseDecision { Baseline, Pending, Accepted, Blacklisted, Expired, Filtered }

public record MediaWatchStatus(string State, int IntervalMinutes, DateTimeOffset? LastCheckedAt,
    DateTimeOffset? NextCheckAt, string? LastError, bool Enabled = true, bool GlobalEnabled = true);

public class WatchSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public int MaxParallelSearches { get; set; } = 3;
    public long TelegramOffset { get; set; }
}

public class MediaWatch
{
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }
    public DateTimeOffset? InitializedAt { get; set; }
    public DateTimeOffset NextCheckAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? LastError { get; set; }
}

public class ReleaseObservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Coverage { get; set; } = string.Empty;
    public ReleaseDecision Decision { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(3);
    public Guid? JobId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }
    public DateTimeOffset NextNotificationAt { get; set; } = DateTimeOffset.UtcNow;
    public int NotificationAttempts { get; set; }
}