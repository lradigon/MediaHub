using MediaHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MediaHub.Infrastructure.Data;

/// <summary>Amorce les données de base (profil de release par défaut).</summary>
public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext db)
    {
        if (!await db.ReleaseProfiles.AnyAsync())
        {
            db.ReleaseProfiles.Add(new ReleaseProfile
            {
                Name = "1080p MULTi x264 (défaut)",
                IsDefault = true
            });
            await db.SaveChangesAsync();
        }
    }
}
