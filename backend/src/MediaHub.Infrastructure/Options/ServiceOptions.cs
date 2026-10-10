namespace MediaHub.Infrastructure.Options;

public class TmdbOptions
{
    public const string Section = "Tmdb";
    /// <summary>Jeton Bearer TMDB v4 (API Read Access Token). NE PAS committer.</summary>
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.themoviedb.org/3";
    public string ImageBaseUrl { get; set; } = "https://image.tmdb.org/t/p/w500";
    public string Language { get; set; } = "fr-FR";
}

public class TorznabOptions
{
    public const string Section = "Torznab";
    /// <summary>Racine de l'indexeur (ex: https://c411.org).</summary>
    public string BaseUrl { get; set; } = "https://c411.org";
    /// <summary>Chemin de l'endpoint Torznab (souvent /api ou /torznab/api).</summary>
    public string ApiPath { get; set; } = "/api";
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>Jeton de compte C411, distinct de la clé Torznab et réservé aux statistiques utilisateur.</summary>
    public string AccountApiToken { get; set; } = string.Empty;
}

public class QBittorrentOptions
{
    public const string Section = "QBittorrent";
    public string BaseUrl { get; set; } = "http://localhost:8080";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = string.Empty;
    /// <summary>Dossier où qBittorrent dépose les fichiers terminés.</summary>
    public string DownloadPath { get; set; } = "/downloads";
}

public class MegaOptions
{
    public const string Section = "Mega";
    /// <summary>Chemin de l'exécutable MEGAcmd (mega-put / mega-ls).</summary>
    public string MegaCmdDir { get; set; } = "/usr/bin";
    /// <summary>Dossier racine distant.</summary>
    public string RemoteRoot { get; set; } = "Films - Séries - Animés";
}

public class HandBrakeOptions
{
    public const string Section = "HandBrake";
    public string CliPath { get; set; } = "HandBrakeCLI";
    public string PresetFilePath { get; set; } = "video.json";
    public string MoviePreset { get; set; } = "Video";
    public string AnimePreset { get; set; } = "Animation";
    /// <summary>Dossier de travail pour les fichiers ré-encodés avant upload.</summary>
    public string WorkingDirectory { get; set; } = "./work";
}

public class FfprobeOptions
{
    public const string Section = "Ffprobe";
    public string CliPath { get; set; } = "ffprobe";
}

public class LibraryOptions
{
    public const string Section = "Library";
    /// <summary>Racine du stockage existant à scanner (lecteur monté ou chemin réseau).</summary>
    public string StorageRoot { get; set; } = @"Z:\Films - Séries - Animés";
    public string SentinelFile { get; set; } = string.Empty;
}
