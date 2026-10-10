using MediaHub.Core;

namespace MediaHub.Core.Releases;

/// <summary>
/// Construit la requête envoyée à l'indexeur (Torznab/C411) pour un média.
/// </summary>
public static class SearchQuery
{
    private static readonly int[] MovieCategories = [2000];
    private static readonly int[] TvCategories = [5000];

    /// <summary>
    /// L'année n'est ajoutée que pour les <b>films</b> : pour une série ou un animé,
    /// les épisodes/saisons s'étalent sur plusieurs années et l'année exclurait les
    /// saisons récentes. La ponctuation gênante (« : », « - »…) est remplacée par des
    /// espaces car de nombreux indexeurs font une correspondance par mots (ET).
    /// </summary>
    public static string ForMedia(string title, int? year, MediaType type)
    {
        var cleaned = Clean(title);
        return type == MediaType.Movie && year is { } y ? $"{cleaned} {y}" : cleaned;
    }

    /// <summary>Catégories Torznab standard associées au type de média demandé.</summary>
    public static IReadOnlyList<int> CategoriesForMedia(MediaType type) =>
        type == MediaType.Movie ? MovieCategories : TvCategories;

    private static string Clean(string title)
    {
        var normalized = new string(
            title.Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : ' ').ToArray());
        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
