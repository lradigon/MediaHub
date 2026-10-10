namespace MediaHub.Core.Entities;

/// <summary>
/// Requête d'ajout formulée par un ami, à valider ou rejeter par un admin.
/// On stocke les infos TMDB brutes car le média n'existe pas encore forcément
/// dans la bibliothèque au moment de la demande.
/// </summary>
public class MediaRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RequestedByUserId { get; set; }
    public User? RequestedBy { get; set; }

    public int TmdbId { get; set; }
    public MediaType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? PosterPath { get; set; }

    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Média créé dans la bibliothèque une fois la requête approuvée.</summary>
    public Guid? MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }
}
