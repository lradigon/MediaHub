namespace MediaHub.Infrastructure.Services;

public record TrackRecommendation(
    IReadOnlyList<int> AudioTrackIndexes,
    IReadOnlyList<int> SubtitleTrackIndexes,
    IReadOnlyList<string> MissingRequirements);

public static class TrackSelectionPolicy
{
    public static TrackRecommendation Recommend(StockMediaFile file, string? originalLanguage)
    {
        var missing = new List<string>();
        var original = NormalizeLanguage(originalLanguage);
        if (original is null) missing.Add("langue originale TMDB inconnue");

        var audio = new List<int>();
        foreach (var language in new[] { original, "fra" }.Where(language => language is not null).Distinct())
        {
            var track = file.AudioTracks.FirstOrDefault(item =>
                !item.IsAudioDescription && NormalizeLanguage(item.Language) == language);
            if (track is null) missing.Add($"audio {language}");
            else audio.Add(track.Index);
        }

        var subtitles = new List<int>();
        foreach (var language in new[] { "fra", "eng" })
        {
            var tracks = file.SubtitleTracks
                .Where(track => NormalizeLanguage(track.Language) == language && !IsSdh(track))
                .OrderBy(SubtitleQuality)
                .ThenBy(track => track.Index)
                .ToList();
            var primary = tracks.FirstOrDefault(track => !track.IsForced) ?? tracks.FirstOrDefault();
            if (primary is null)
            {
                missing.Add($"sous-titre {language}");
                continue;
            }
            subtitles.Add(primary.Index);
            var forced = tracks.FirstOrDefault(track => track.IsForced && track.Index != primary.Index);
            if (forced is not null) subtitles.Add(forced.Index);
        }
        return new(audio, subtitles, missing);
    }

    public static bool IsSdh(StockSubtitleTrack track) =>
        track.Source?.Contains("SDH", StringComparison.OrdinalIgnoreCase) == true;

    private static int SubtitleQuality(StockSubtitleTrack track) =>
        track.Source?.Contains("SRT", StringComparison.OrdinalIgnoreCase) == true ||
        track.Format?.Contains("SRT", StringComparison.OrdinalIgnoreCase) == true ? 0 :
        track.Format?.Contains("text", StringComparison.OrdinalIgnoreCase) == true ? 1 : 2;

    private static string? NormalizeLanguage(string? language) => language?.ToLowerInvariant() switch
    {
        "fr" or "fre" or "fra" => "fra",
        "en" or "eng" => "eng",
        "ja" or "jpn" => "jpn",
        "ko" or "kor" => "kor",
        "es" or "spa" => "spa",
        "de" or "deu" or "ger" => "deu",
        "it" or "ita" => "ita",
        { Length: 3 } code => code,
        _ => null
    };
}