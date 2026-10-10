using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MediaHub.Core;
using MediaHub.Core.Abstractions;
using MediaHub.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace MediaHub.Infrastructure.Clients;

/// <summary>Implémentation TMDB via l'API v3 avec jeton Bearer v4.</summary>
public class TmdbClient(HttpClient http, IOptions<TmdbOptions> options) : ITmdbClient
{
    private readonly string _language = options.Value.Language;

    public async Task CheckConnectionAsync(CancellationToken ct = default)
    {
        await http.GetFromJsonAsync<System.Text.Json.JsonElement>("/3/configuration", ct);
    }

    public async Task<IReadOnlyList<TmdbSearchItem>> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = Localized($"/3/search/multi?query={Uri.EscapeDataString(query)}");
        var result = await http.GetFromJsonAsync<TmdbListResponse>(url, ct);
        if (result?.Results is null) return Array.Empty<TmdbSearchItem>();

        return result.Results
            .Where(r => r.MediaType is "movie" or "tv")
            .Select(Map)
            .ToList();
    }

    public async Task<TmdbSearchItem?> GetMovieAsync(int tmdbId, CancellationToken ct = default)
    {
        var r = await GetLocalizedAsync<TmdbResult>($"/3/movie/{tmdbId}", ct);
        if (r is null) return null;
        r.MediaType = "movie";
        return Map(r);
    }

    public async Task<string?> GetMovieDirectorAsync(int tmdbId, CancellationToken ct = default)
    {
        var credits = await GetLocalizedAsync<TmdbCredits>($"/3/movie/{tmdbId}/credits", ct);
        return credits?.Crew
            ?.FirstOrDefault(person => string.Equals(person.Job, "Director", StringComparison.OrdinalIgnoreCase))
            ?.Name;
    }

    public async Task<TmdbSearchItem?> GetTvAsync(int tmdbId, CancellationToken ct = default)
    {
        var r = await GetLocalizedAsync<TmdbResult>($"/3/tv/{tmdbId}", ct);
        if (r is null) return null;
        r.MediaType = "tv";
        return Map(r);
    }

    public async Task<TmdbEpisodeInfo?> GetEpisodeAsync(int tvId, int season, int episode, CancellationToken ct = default)
    {
        var r = await GetLocalizedAsync<TmdbEpisodeResult>(
            $"/3/tv/{tvId}/season/{season}/episode/{episode}", ct);
        if (r is null) return null;
        return new TmdbEpisodeInfo(season, episode, r.Name, ParseDate(r.AirDate));
    }

    public async Task<IReadOnlyList<TmdbSeason>> GetSeasonsAsync(int tvId, CancellationToken ct = default)
    {
        var details = await GetLocalizedAsync<TmdbTvDetails>($"/3/tv/{tvId}", ct);
        if (details?.Seasons is null) return Array.Empty<TmdbSeason>();

        var seasons = new List<TmdbSeason>();
        foreach (var s in details.Seasons.OrderBy(x => x.SeasonNumber))
        {
            var detail = await GetLocalizedAsync<TmdbSeasonDetails>(
                $"/3/tv/{tvId}/season/{s.SeasonNumber}", ct);
            var episodes = (detail?.Episodes ?? [])
                .Select(e => new TmdbEpisodeInfo(s.SeasonNumber, e.EpisodeNumber, e.Name, ParseDate(e.AirDate)))
                .ToList();
            seasons.Add(new TmdbSeason(s.SeasonNumber, s.Name, episodes));
        }
        return seasons;
    }

    private static TmdbSearchItem Map(TmdbResult r)
    {
        var type = r.MediaType == "tv" ? MediaType.Series : MediaType.Movie;
        var title = r.Title ?? r.Name ?? string.Empty;
        var originalTitle = r.OriginalTitle ?? r.OriginalName;
        var date = r.ReleaseDate ?? r.FirstAirDate;
        var releaseDate = ParseDate(date);
        int? year = releaseDate?.Year;

        return new TmdbSearchItem(
            r.Id, type, title, originalTitle, r.OriginalLanguage, year, releaseDate,
            r.Overview, r.PosterPath, r.BackdropPath);
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var d) ? d : null;

    private string Localized(string path) =>
        $"{path}{(path.Contains('?') ? '&' : '?')}language={Uri.EscapeDataString(_language)}";

    private async Task<T?> GetLocalizedAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            return await http.GetFromJsonAsync<T>(Localized(path), ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private sealed class TmdbListResponse
    {
        [JsonPropertyName("results")] public List<TmdbResult>? Results { get; set; }
    }

    private sealed class TmdbResult
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("media_type")] public string? MediaType { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
        [JsonPropertyName("original_name")] public string? OriginalName { get; set; }
        [JsonPropertyName("original_language")] public string? OriginalLanguage { get; set; }
        [JsonPropertyName("overview")] public string? Overview { get; set; }
        [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
        [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; set; }
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
        [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; set; }
    }

    private sealed class TmdbEpisodeResult
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("air_date")] public string? AirDate { get; set; }
    }

    private sealed class TmdbCredits
    {
        [JsonPropertyName("crew")] public List<TmdbCrewMember>? Crew { get; set; }
    }

    private sealed class TmdbCrewMember
    {
        [JsonPropertyName("job")] public string? Job { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class TmdbTvDetails
    {
        [JsonPropertyName("seasons")] public List<TmdbSeasonSummary>? Seasons { get; set; }
    }

    private sealed class TmdbSeasonSummary
    {
        [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("episode_count")] public int EpisodeCount { get; set; }
    }

    private sealed class TmdbSeasonDetails
    {
        [JsonPropertyName("episodes")] public List<TmdbEpisodeItem>? Episodes { get; set; }
    }

    private sealed class TmdbEpisodeItem
    {
        [JsonPropertyName("episode_number")] public int EpisodeNumber { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("air_date")] public string? AirDate { get; set; }
    }
}
