namespace MediaHub.Core.Entities;

/// <summary>
/// Reflet local de l'inventaire du cloud (MEGA). Rafraîchi périodiquement
/// pour savoir ce qui est réellement stocké sans interroger MEGA à chaque fois.
/// </summary>
public class CloudFile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Chemin complet côté MEGA (ex: "Films - Séries - Animés/Films/...").</summary>
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    public Guid? MediaItemId { get; set; }
    public Guid? EpisodeId { get; set; }

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}
