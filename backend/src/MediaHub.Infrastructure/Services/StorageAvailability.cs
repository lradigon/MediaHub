using MediaHub.Infrastructure.Options;

namespace MediaHub.Infrastructure.Services;

public static class StorageAvailability
{
    public static void EnsureAvailable(LibraryOptions options)
    {
        if (!Directory.Exists(options.StorageRoot) ||
            (!string.IsNullOrWhiteSpace(options.SentinelFile) &&
             !File.Exists(Path.Combine(options.StorageRoot, options.SentinelFile))))
            throw new DirectoryNotFoundException("Stockage indisponible : vérifier le montage et sa sentinelle avant de synchroniser.");
    }
}