namespace MediaHub.Core.Entities;

public class StockInspection
{
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }
    public Guid Generation { get; set; } = Guid.NewGuid();
    public string Status { get; set; } = "Queued";
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? CurrentFile { get; set; }
    public string? Error { get; set; }
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int InspectedFiles { get; set; }
    public int FailedFiles { get; set; }
    public int? MaxFiles { get; set; }
    public bool Force { get; set; }
    public Guid? SelectedFileId { get; set; }
    public string FileIdsJson { get; set; } = "[]";
}

public class StockFileInspection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public DateTimeOffset? InspectedAt { get; set; }
    public string Status { get; set; } = "Pending";
    public string? DetailsJson { get; set; }
    public string? Error { get; set; }
    public bool IsPresent { get; set; } = true;
}