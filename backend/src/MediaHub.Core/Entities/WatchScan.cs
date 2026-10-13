namespace MediaHub.Core.Entities;

public class WatchScan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string State { get; set; } = "queued";
    public bool Manual { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public string? Detail { get; set; }
    public ICollection<WatchScanItem> Items { get; set; } = new List<WatchScanItem>();
}

public class WatchScanItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WatchScanId { get; set; }
    public WatchScan? WatchScan { get; set; }
    public Guid MediaItemId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string State { get; set; } = "queued";
    public string Detail { get; set; } = "En attente de traitement";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int Observations { get; set; }
    public int Found { get; set; }
    public int Filtered { get; set; }
    public int NewProposals { get; set; }
}

public record ReleaseWatchScanResult(int Found, int Matching, int Filtered, int NewProposals, bool Baseline);