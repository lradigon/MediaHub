namespace MediaHub.Core.Abstractions;

public record CloudEntry(string Path, long SizeBytes);

/// <summary>Stockage cloud (MEGA via MEGAcmd). Upload et inventaire.</summary>
public interface ICloudStorage
{
    /// <summary>Envoie un fichier local vers un dossier distant. Renvoie le chemin distant.</summary>
    Task<string> UploadAsync(
        string localPath,
        string remoteFolder,
        string? remoteFileName = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>Liste récursivement le contenu d'un dossier distant.</summary>
    Task<IReadOnlyList<CloudEntry>> ListAsync(string remoteFolder, CancellationToken ct = default);
}
