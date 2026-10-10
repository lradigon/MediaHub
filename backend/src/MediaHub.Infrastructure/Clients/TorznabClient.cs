using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MediaHub.Core.Abstractions;
using MediaHub.Core.Releases;
using MediaHub.Infrastructure.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace MediaHub.Infrastructure.Clients;

/// <summary>
/// Client Torznab générique (compatible C411). Torznab est un flux RSS/XML :
/// on interroge l'endpoint et on mappe chaque &lt;item&gt; en ReleaseCandidate.
/// </summary>
public class TorznabClient(HttpClient http, IOptions<TorznabOptions> options, IMemoryCache cache) : ITorznabClient
{
    private readonly TorznabOptions _opt = options.Value;
    private static readonly XNamespace TorznabNs = "http://torznab.com/schemas/2015/feed";
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public async Task CheckConnectionAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{_opt.ApiPath}?t=caps&apikey={Uri.EscapeDataString(_opt.ApiKey)}", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
        if (document.Root?.Name.LocalName != "caps")
            throw new InvalidOperationException("C411 n'a pas renvoyé de capacités Torznab valides.");
    }

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        string query, IEnumerable<int>? categories = null, bool tvSearch = false, bool forceRefresh = false, int? tmdbId = null, int? seasonNumber = null, CancellationToken ct = default)
    {
        var cat = categories is null ? string.Empty : $"&cat={string.Join(",", categories)}";
        var searchType = tvSearch ? "tvsearch" : "search";
        var isEpisodeSearch = Regex.IsMatch(query, @"\bS\d{1,3}E\d{1,4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var cacheKey = $"torznab:{searchType}:{query}:{cat}:tmdb:{tmdbId}:season:{seasonNumber}";
        if (!forceRefresh && cache.TryGetValue<IReadOnlyList<ReleaseCandidate>>(cacheKey, out var cached) && cached is not null)
            return cached;

        await RequestGate.WaitAsync(ct);
        try
        {
            // Une seconde requête identique a pu remplir le cache pendant l'attente.
            if (!forceRefresh && cache.TryGetValue<IReadOnlyList<ReleaseCandidate>>(cacheKey, out cached) && cached is not null)
                return cached;

            var items = await FetchItemsAsync(query, searchType, cat, null, null, ct);
            if (tmdbId is > 0 && !isEpisodeSearch)
            {
                var tmdbSearchType = tvSearch ? "tvsearch" : "movie";
                var tmdbSearchItems = await FetchItemsAsync(query, tmdbSearchType, cat, null, tmdbId, ct);
                items = items
                    .Concat(tmdbSearchItems)
                    .GroupBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            }

            if (tvSearch && !isEpisodeSearch)
            {
                if (seasonNumber is > 0)
                {
                    var seasonItems = await FetchItemsAsync(query, searchType, cat, seasonNumber, null, ct);
                    items = items
                        .Concat(seasonItems)
                        .GroupBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .ToList();
                }

                // Le flux RSS de catégorie est alimenté par les nouveautés et peut précéder
                // l'index de recherche C411. On le filtre localement pour la série demandée.
                var rssItems = await FetchItemsAsync(null, "search", cat, null, null, ct);
                items = items
                    .Concat(rssItems.Where(item => MatchesQuery(item.Title, query)))
                    .GroupBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            }

            if (tmdbId is > 0 && !isEpisodeSearch)
            {
                var tmdbItems = await FetchTmdbItemsAsync(tmdbId.Value, tvSearch ? "tv" : "movie", ct);
                items = items
                    .Concat(tmdbItems)
                    .GroupBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            }

            cache.Set(cacheKey, (IReadOnlyList<ReleaseCandidate>)items, TimeSpan.FromMinutes(2));
            return items;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private async Task<List<ReleaseCandidate>> FetchItemsAsync(
        string? query, string searchType, string categories, int? seasonNumber, int? tmdbId, CancellationToken ct)
    {
        var delay = _lastRequestAt.AddSeconds(2) - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);

        _lastRequestAt = DateTimeOffset.UtcNow;
        var queryParameter = string.IsNullOrWhiteSpace(query) ? string.Empty : $"&q={Uri.EscapeDataString(query)}";
        var seasonParameter = seasonNumber is > 0 ? $"&season={seasonNumber}" : string.Empty;
        var tmdbParameter = tmdbId is > 0 ? $"&tmdbid={tmdbId}" : string.Empty;
        var url = $"{_opt.ApiPath}?t={searchType}&apikey={Uri.EscapeDataString(_opt.ApiKey)}{queryParameter}{categories}{seasonParameter}{tmdbParameter}&limit=100";

        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("C411 limite temporairement les recherches.", null, response.StatusCode);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.OrdinalIgnoreCase) == true)
            throw new HttpRequestException("C411 a renvoyé une page HTML au lieu du flux Torznab (site indisponible ou accès à l'API bloqué).", null, System.Net.HttpStatusCode.BadGateway);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        XDocument document;
        try
        {
            document = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
        }
        catch (XmlException ex)
        {
            throw new HttpRequestException("C411 a renvoyé un flux Torznab invalide (page HTML ou réponse incomplète).", ex, System.Net.HttpStatusCode.BadGateway);
        }
        if (document.Root?.Name.LocalName is not "rss" and not "feed")
            throw new HttpRequestException("C411 n'a pas renvoyé de flux Torznab valide.", null, System.Net.HttpStatusCode.BadGateway);
        var items = document.Descendants("item").Select(MapItem).ToList();
        foreach (var item in items)
            ReleaseParser.Parse(item);
        return items;
    }

    private async Task<List<ReleaseCandidate>> FetchTmdbItemsAsync(int tmdbId, string tmdbType, CancellationToken ct)
    {
        var delay = _lastRequestAt.AddSeconds(2) - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);

        _lastRequestAt = DateTimeOffset.UtcNow;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/torrents/by-tmdb?tmdbId={tmdbId}&tmdbType={tmdbType}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _opt.ApiKey);

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("C411 limite temporairement les recherches.", null, response.StatusCode);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
            return [];

        var items = releases.EnumerateArray().Select(MapTmdbItem).ToList();
        foreach (var item in items)
            ReleaseParser.Parse(item);
        return items;
    }

    private static bool MatchesQuery(string title, string query)
    {
        var titleWords = NormalizeWords(title);
        return NormalizeWords(query).All(titleWords.Contains);
    }

    private static HashSet<string> NormalizeWords(string value) =>
        new string(value.Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

    public async Task<C411AccountStats> GetAccountStatsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.AccountApiToken))
            throw new InvalidOperationException("Le jeton d'API de compte C411 n'est pas configuré.");

        var url = $"/api/v1/user?api_token={Uri.EscapeDataString(_opt.AccountApiToken)}";
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Le jeton d'API de compte C411 est invalide ou expiré.");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var account = document.RootElement.TryGetProperty("data", out var data) ? data : document.RootElement;

        return new C411AccountStats(
            ReadString(account, "username") ?? "C411",
            ReadLong(account, "uploaded"),
            ReadLong(account, "downloaded"),
            ReadDouble(account, "ratio"));
    }

    private static ReleaseCandidate MapItem(XElement item)
    {
        var title = item.Element("title")?.Value ?? string.Empty;
        var link = item.Element("link")?.Value;
        var comments = item.Element("comments")?.Value;

        // Le lien de téléchargement réel (.torrent/magnet) est dans <enclosure>,
        // pas dans <link> (qui pointe vers la page de détail du torrent).
        var enclosure = item.Element("enclosure")?.Attribute("url")?.Value;

        long size = ParseLong(item.Element("size")?.Value) ?? Attr(item, "size");
        var seeders = (int)Attr(item, "seeders");
        var peers = (int)Attr(item, "peers");
        var grabs = (int)Attr(item, "grabs");
        var dvf = AttrOrNull(item, "downloadvolumefactor");
        var pubDate = DateTimeOffset.TryParse(
            item.Element("pubDate")?.Value, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : (DateTimeOffset?)null;

        return new ReleaseCandidate
        {
            Title = title,
            DownloadUrl = enclosure ?? link,
            InfoUrl = comments ?? link,
            SizeBytes = size,
            Seeders = seeders,
            Leechers = Math.Max(0, peers - seeders),
            Grabs = grabs,
            IsFreeleech = dvf == 0,
            PublishDate = pubDate
        };
    }

    private static ReleaseCandidate MapTmdbItem(JsonElement item)
    {
        var createdAt = ReadString(item, "createdAt");
        return new ReleaseCandidate
        {
            Title = ReadString(item, "name") ?? string.Empty,
            InfoUrl = ReadString(item, "url"),
            SizeBytes = ReadLong(item, "size"),
            Seeders = (int)ReadLong(item, "seeders"),
            Leechers = (int)ReadLong(item, "leechers"),
            Grabs = (int)ReadLong(item, "completed"),
            PublishDate = DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null
        };
    }

    /// <summary>Lit un attribut torznab &lt;torznab:attr name="x" value="y"/&gt;.</summary>
    private static long Attr(XElement item, string name)
    {
        var el = item.Elements(TorznabNs + "attr")
            .FirstOrDefault(a => (string?)a.Attribute("name") == name);
        return ParseLong((string?)el?.Attribute("value")) ?? 0;
    }

    /// <summary>Comme Attr mais renvoie null si l'attribut est absent (pour distinguer 0 de "non fourni").</summary>
    private static long? AttrOrNull(XElement item, string name)
    {
        var el = item.Elements(TorznabNs + "attr")
            .FirstOrDefault(a => (string?)a.Attribute("name") == name);
        return el is null ? null : ParseLong((string?)el.Attribute("value"));
    }

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? ReadString(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) ? value.ToString() : null;

    private static long ReadLong(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static double ReadDouble(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
}
