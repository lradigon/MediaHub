using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaHub.Core.Entities;

namespace MediaHub.Core.Releases;

public static class ReleaseWatchPolicy
{
    public static string Fingerprint(ReleaseCandidate release) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Normalize(release.Title)}|{release.SizeBytes}")));

    public static ReleaseDecision InitialDecision(bool initialized, bool eligible) =>
        !initialized ? ReleaseDecision.Baseline : eligible ? ReleaseDecision.Pending : ReleaseDecision.Filtered;

    public static bool Matches(MediaItem media, ReleaseCandidate release)
    {
        var title = Normalize(release.Title);
        var matchesTitle = new[] { media.Title, media.OriginalTitle }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Normalize(value!))
            .Any(value => title == value || title.StartsWith(value + " ", StringComparison.Ordinal));
        if (!matchesTitle) return false;
        if (media.Type != MediaType.Movie)
            return release.SeasonNumber.HasValue || release.EpisodeNumber.HasValue || release.IsCompletePack;
        var years = Regex.Matches(release.Title, @"\b(?:19|20)\d{2}\b").Select(match => int.Parse(match.Value));
        return media.Year is null || !years.Any() || years.Contains(media.Year.Value);
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
}