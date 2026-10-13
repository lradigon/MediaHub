using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MediaHub.Core.Abstractions;
using MediaHub.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace MediaHub.Infrastructure.Clients;

/// <summary>
/// Pilote qBittorrent via son API Web v2. Le HttpClient est configuré avec un
/// CookieContainer partagé (voir DependencyInjection) pour conserver le SID.
/// </summary>
public class QBittorrentClient(HttpClient http, IOptions<QBittorrentOptions> options) : IDownloadClient
{
    private readonly QBittorrentOptions _opt = options.Value;
    private bool _authenticated;

    public async Task CheckConnectionAsync(CancellationToken ct = default)
    {
        await EnsureLoginAsync(ct);
        using var response = await http.GetAsync("/api/v2/app/version", ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task EnsureLoginAsync(CancellationToken ct)
    {
        if (_authenticated) return;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = _opt.Username,
            ["password"] = _opt.Password
        });
        using var resp = await http.PostAsync("/api/v2/auth/login", form, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.StatusCode == HttpStatusCode.NoContent)
        {
            using var verification = await http.GetAsync("/api/v2/app/version", ct);
            verification.EnsureSuccessStatusCode();
        }
        else if (!string.Equals((await resp.Content.ReadAsStringAsync(ct)).Trim(), "Ok.", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Identifiants qBittorrent invalides.");
        _authenticated = true;
    }

    public async Task<string> AddTorrentAsync(string urlOrMagnet, string? savePath = null, CancellationToken ct = default)
    {
        await EnsureLoginAsync(ct);

        // Snapshot des hash existants pour identifier le nouveau torrent ajouté
        // (qBittorrent ne renvoie pas le hash à l'ajout, surtout pour un .torrent).
        var before = await GetHashesAsync(ct);

        var fields = new Dictionary<string, string> { ["urls"] = urlOrMagnet };
        if (!string.IsNullOrWhiteSpace(savePath)) fields["savepath"] = savePath;

        using var form = new FormUrlEncodedContent(fields);
        using var resp = await http.PostAsync("/api/v2/torrents/add", form, ct);
        resp.EnsureSuccessStatusCode();

        // Pour un magnet, le hash est directement extractible.
        var magnetHash = ExtractHashFromMagnet(urlOrMagnet);
        if (magnetHash is not null)
            return magnetHash;

        // Sinon (URL .torrent), on attend qu'un nouveau hash apparaisse.
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500, ct);
            var now = await GetHashesAsync(ct);
            var added = now.Except(before).ToList();
            if (added.Count > 0)
                return added[0];
        }

        throw new InvalidOperationException(
            "Torrent ajouté mais impossible de résoudre son hash (aucun nouveau torrent détecté).");
    }

    private async Task<HashSet<string>> GetHashesAsync(CancellationToken ct)
    {
        var list = await http.GetFromJsonAsync<List<TorrentInfoDto>>("/api/v2/torrents/info", ct)
                   ?? [];
        return list.Select(t => t.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<TorrentStatus?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        await EnsureLoginAsync(ct);
        var list = await http.GetFromJsonAsync<List<TorrentInfoDto>>("/api/v2/torrents/info", ct);
        var torrent = list?.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        return torrent is null ? null : ToStatus(torrent);
    }

    public async Task<TorrentStatus?> GetStatusAsync(string hash, CancellationToken ct = default)
    {
        await EnsureLoginAsync(ct);
        var list = await http.GetFromJsonAsync<List<TorrentInfoDto>>(
            $"/api/v2/torrents/info?hashes={hash}", ct);
        var t = list?.FirstOrDefault();
        return t is null ? null : ToStatus(t);
    }

    private static TorrentStatus ToStatus(TorrentInfoDto torrent) =>
        new(
            torrent.Hash, torrent.Name, torrent.Progress, torrent.Progress >= 1.0, torrent.ContentPath,
            torrent.NumSeeds, torrent.Size, torrent.Downloaded, torrent.DownloadSpeed);

    public async Task RemoveAsync(string hash, bool deleteFiles, CancellationToken ct = default)
    {
        await EnsureLoginAsync(ct);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["hashes"] = hash,
            ["deleteFiles"] = deleteFiles ? "true" : "false"
        });
        using var resp = await http.PostAsync("/api/v2/torrents/delete", form, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static string? ExtractHashFromMagnet(string magnet)
    {
        const string marker = "btih:";
        var i = magnet.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var start = i + marker.Length;
        var end = magnet.IndexOf('&', start);
        return (end < 0 ? magnet[start..] : magnet[start..end]).ToLowerInvariant();
    }

    private sealed class TorrentInfoDto
    {
        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("content_path")]
        public string? ContentPath { get; set; }

        [JsonPropertyName("num_seeds")]
        public int NumSeeds { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("downloaded")]
        public long Downloaded { get; set; }

        [JsonPropertyName("dlspeed")]
        public long DownloadSpeed { get; set; }
    }
}
