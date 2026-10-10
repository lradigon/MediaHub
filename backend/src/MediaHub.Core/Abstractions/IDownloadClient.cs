namespace MediaHub.Core.Abstractions;

public record TorrentStatus(
    string Hash, string Name, double Progress, bool IsComplete, string? ContentPath,
    int Seeders = 0, long SizeBytes = 0, long DownloadedBytes = 0, long DownloadSpeed = 0);

/// <summary>Client de téléchargement (qBittorrent) piloté via son API Web.</summary>
public interface IDownloadClient
{
    Task CheckConnectionAsync(CancellationToken ct = default);
    /// <summary>Ajoute un torrent (via URL/magnet) et renvoie son hash.</summary>
    Task<string> AddTorrentAsync(string urlOrMagnet, string? savePath = null, CancellationToken ct = default);

    Task<TorrentStatus?> FindByNameAsync(string name, CancellationToken ct = default);

    Task<TorrentStatus?> GetStatusAsync(string hash, CancellationToken ct = default);

    Task RemoveAsync(string hash, bool deleteFiles, CancellationToken ct = default);
}
