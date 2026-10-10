namespace MediaHub.Core.Entities;

public class Season
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    public int SeasonNumber { get; set; }
    public string? Title { get; set; }

    public ICollection<Episode> Episodes { get; set; } = new List<Episode>();
}
