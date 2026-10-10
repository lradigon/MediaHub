namespace MediaHub.Core.Entities;

public class Episode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SeasonId { get; set; }
    public Season? Season { get; set; }

    public int EpisodeNumber { get; set; }
    public string? Title { get; set; }
    public DateOnly? AirDate { get; set; }

    public EpisodeStatus Status { get; set; } = EpisodeStatus.Missing;

    /// <summary>Chemin du fichier final sur le cloud, une fois uploadé.</summary>
    public string? CloudPath { get; set; }
}
