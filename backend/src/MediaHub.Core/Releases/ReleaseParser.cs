using System.Globalization;
using System.Text.RegularExpressions;
using MediaHub.Core.Entities;

namespace MediaHub.Core.Releases;

/// <summary>
/// Extraction d'attributs depuis un nom de release et application d'un profil.
/// C'est l'équivalent minimal du "release parsing" des *arr. Volontairement
/// simple : à enrichir au fil des conventions de nommage rencontrées sur C411.
/// </summary>
public static partial class ReleaseParser
{
    [GeneratedRegex(@"\b(2160p|4k|uhd|1080p|720p|480p)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();

    [GeneratedRegex(@"\b(x265|h\.?265|hevc|x264|h\.?264|avc|av1|xvid|divx|mpeg-?2)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CodecRegex();

    [GeneratedRegex(@"\b(remux|blu-?ray|bd-?rip|br-?rip|web-?dl|web-?rip|web|hdtv|dvd-?rip|cam|hdts|telesync|ts)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRegex();

    [GeneratedRegex(@"(?<![a-z0-9])multi(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex MultiRegex();

    [GeneratedRegex(@"\bvfq\b", RegexOptions.IgnoreCase)]
    private static partial Regex QuebecFrenchRegex();

    [GeneratedRegex(@"\b(DTS[-. ]?HD[-. ]?MA|DTS[-. ]?HD|TrueHD|Atmos|DDP|E-?AC-?3|EAC3|AC-?3|DTS|AAC|FLAC|Opus|LPCM|MP3)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AudioCodecRegex();

    [GeneratedRegex(@"\b([1-9])\.([0-2])\b")]
    private static partial Regex AudioChannelsRegex();

    [GeneratedRegex(@"\b(HDR10\+|HDR10|HDR|Dolby[.\s]?Vision|DoVi|DV)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DynamicRangeRegex();

    [GeneratedRegex(@"\b(8|10|12)[-. ]?bits?\b", RegexOptions.IgnoreCase)]
    private static partial Regex BitDepthRegex();

    [GeneratedRegex(@"[Ss](\d{1,2})[\s._-]?[Ee](\d{1,4})")]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"\b(int[ée]grale|complete|complet|full[\s._-]?series)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompleteRegex();

    [GeneratedRegex(@"[Ss](\d{1,2})[\s._-]*-[\s._-]*[Ss](\d{1,2})")]
    private static partial Regex SeasonRangeRegex();

    [GeneratedRegex(@"(?:saison|season)[\s._-]*(\d{1,2})|[Ss](\d{1,2})(?![\s._-]*[Ee]?\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonOnlyRegex();

    [GeneratedRegex(@"-([A-Za-z0-9]+)$")]
    private static partial Regex GroupRegex();

    /// <summary>Extrait les attributs connus du nom d'une release.</summary>
    public static ReleaseCandidate Parse(ReleaseCandidate release)
    {
        var title = release.Title;

        release.Resolution = Normalize(ResolutionRegex().Match(title).Value);
        release.VideoCodec = NormalizeCodec(CodecRegex().Match(title).Value);
        release.Source = NormalizeSource(SourceRegex().Match(title).Value);
        release.IsMulti = MultiRegex().IsMatch(title);

        release.AudioCodec = NormalizeAudio(AudioCodecRegex().Match(title).Value);
        var channels = AudioChannelsRegex().Match(title);
        if (channels.Success) release.AudioChannels = channels.Value;
        release.DynamicRange = NormalizeDynamicRange(DynamicRangeRegex().Match(title).Value);
        var bits = BitDepthRegex().Match(title);
        if (bits.Success) release.BitDepth = int.Parse(bits.Groups[1].Value, CultureInfo.InvariantCulture);

        var group = GroupRegex().Match(title);
        if (group.Success)
            release.ReleaseGroup = group.Groups[1].Value;

        var se = SeasonEpisodeRegex().Match(title);
        if (se.Success)
        {
            // Épisode précis (SxxEyy).
            release.SeasonNumber = int.Parse(se.Groups[1].Value, CultureInfo.InvariantCulture);
            release.EpisodeNumber = int.Parse(se.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            var range = SeasonRangeRegex().Match(title);
            if (range.Success)
            {
                // Pack multi-saisons (ex: S01-S05).
                release.IsCompletePack = true;
                release.SeasonNumber = int.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
                release.SeasonEnd = int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            else if (CompleteRegex().IsMatch(title))
            {
                // Intégrale / série complète.
                release.IsCompletePack = true;
            }
            else
            {
                var so = SeasonOnlyRegex().Match(title);
                if (so.Success)
                {
                    // Pack d'une saison entière (Sxx sans épisode).
                    var g = so.Groups[1].Success ? so.Groups[1] : so.Groups[2];
                    release.SeasonNumber = int.Parse(g.Value, CultureInfo.InvariantCulture);
                }
            }
        }

        return release;
    }

    /// <summary>Décrit ce que couvre une release : intégrale, saison complète ou épisode.</summary>
    public static string DescribeCoverage(ReleaseCandidate r)
    {
        if (r.IsCompletePack)
            return r.SeasonNumber is { } a && r.SeasonEnd is { } b ? $"Saisons {a}–{b}" : "Intégrale";
        if (r.SeasonNumber is { } s && r.EpisodeNumber is { } e)
            return $"S{s:D2}E{e:D2}";
        if (r.SeasonNumber is { } sp)
            return $"Saison {sp} complète";
        return "—";
    }

    /// <summary>
    /// Évalue une release contre un profil : accepte/rejette et calcule un score
    /// (plus le score est haut, meilleure est la release parmi les acceptées).
    /// </summary>
    public static ReleaseEvaluation Evaluate(ReleaseCandidate release, ReleaseProfile profile)
    {
        Parse(release);
        var title = release.Title;
        var reasons = new List<string>();

        if (QuebecFrenchRegex().IsMatch(title))
            reasons.Add("Audio VFQ (français québécois) rejeté");

        if (profile.RequireMulti && !release.IsMulti)
            reasons.Add("MULTi requis mais absent");

        // Codec : comparaison sur l'attribut parsé et normalisé (AVC->x264, HEVC->x265...).
        if (release.VideoCodec is not null &&
            profile.RejectedVideoCodecs.Any(c => string.Equals(c, release.VideoCodec, StringComparison.OrdinalIgnoreCase)))
            reasons.Add($"Codec rejeté: {release.VideoCodec}");

        if (profile.AllowedVideoCodecs.Count > 0 &&
            (release.VideoCodec is null ||
             !profile.AllowedVideoCodecs.Any(c => string.Equals(c, release.VideoCodec, StringComparison.OrdinalIgnoreCase))))
            reasons.Add("Aucun codec autorisé détecté");

        // Résolution : attribut parsé.
        if (profile.AllowedResolutions.Count > 0 &&
            (release.Resolution is null ||
             !profile.AllowedResolutions.Any(r => string.Equals(r, release.Resolution, StringComparison.OrdinalIgnoreCase))))
            reasons.Add("Résolution non autorisée");

        // Source : attribut parsé (normalisé : BluRay ≈ Blu-ray ≈ BD Rip).
        if (profile.AllowedSources.Count > 0 &&
            (release.Source is null ||
             !profile.AllowedSources.Any(s => string.Equals(s, release.Source, StringComparison.OrdinalIgnoreCase))))
            reasons.Add("Source non autorisée");

        // Termes rejetés : correspondance par mot entier (évite que "TS" matche dans "DTS").
        if (ContainsAnyWord(title, profile.RejectedTerms, out var badTerm))
            reasons.Add($"Terme rejeté: {badTerm}");

        if (release.Seeders < profile.MinSeeders)
            reasons.Add($"Seeders insuffisants ({release.Seeders} < {profile.MinSeeders})");

        var accepted = reasons.Count == 0;
        var score = accepted ? ComputeScore(release) : 0;

        return new ReleaseEvaluation
        {
            Release = release,
            Accepted = accepted,
            Score = score,
            RejectionReasons = reasons
        };
    }

    private static int ComputeScore(ReleaseCandidate release)
    {
        var score = release.Seeders;
        if (release.IsMulti) score += 50;
        if (string.Equals(release.Resolution, "1080p", StringComparison.OrdinalIgnoreCase)) score += 30;
        if (release.Source is "Remux") score += 20;
        else if (release.Source is "BluRay") score += 15;
        else if (release.Source is "WEB-DL") score += 10;
        return score;
    }

    private static bool ContainsAnyWord(string title, IEnumerable<string> terms, out string? matched)
    {
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            // Bornes alphanumériques : "TS" ne matche pas dans "DTS", mais ".TS." ou "-TS-" oui.
            var pattern = $@"(?<![A-Za-z0-9]){Regex.Escape(term)}(?![A-Za-z0-9])";
            if (Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase))
            {
                matched = term;
                return true;
            }
        }
        matched = null;
        return false;
    }

    private static string StripSeparators(string value) =>
        value.Replace("-", string.Empty).Replace(".", string.Empty)
             .Replace("_", string.Empty).Replace(" ", string.Empty);

    private static string? Normalize(string value) =>
        string.IsNullOrEmpty(value) ? null : value.ToLowerInvariant();

    private static string? NormalizeCodec(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var v = value.ToLowerInvariant().Replace(".", string.Empty);
        return v switch
        {
            "h265" or "hevc" => "x265",
            "h264" or "avc" => "x264",
            _ => v
        };
    }

    private static string? NormalizeSource(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var v = StripSeparators(value).ToLowerInvariant();
        return v switch
        {
            "remux" => "Remux",
            "bluray" or "bdrip" or "brrip" => "BluRay",
            "webdl" => "WEB-DL",
            "webrip" => "WEBRip",
            "web" => "WEB",
            "hdtv" => "HDTV",
            "dvdrip" => "DVDRip",
            _ => value
        };
    }

    private static string? NormalizeAudio(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var v = StripSeparators(value).ToLowerInvariant();
        return v switch
        {
            "dtshdma" => "DTS-HD MA",
            "dtshd" => "DTS-HD",
            "truehd" => "TrueHD",
            "atmos" => "Atmos",
            "eac3" or "ddp" => "EAC3",
            "ac3" => "AC3",
            "dts" => "DTS",
            "aac" => "AAC",
            "flac" => "FLAC",
            "opus" => "Opus",
            "lpcm" => "LPCM",
            "mp3" => "MP3",
            _ => value.ToUpperInvariant()
        };
    }

    private static string? NormalizeDynamicRange(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var v = StripSeparators(value).ToLowerInvariant();
        return v switch
        {
            "hdr10plus" => "HDR10+",
            "hdr10" => "HDR10",
            "hdr" => "HDR",
            "dolbyvision" or "dovi" or "dv" => "DV",
            _ => value.ToUpperInvariant()
        };
    }
}
