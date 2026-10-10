using MediaHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MediaHub.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<MediaRequest> MediaRequests => Set<MediaRequest>();
    public DbSet<PipelineJob> PipelineJobs => Set<PipelineJob>();
    public DbSet<CloudFile> CloudFiles => Set<CloudFile>();
    public DbSet<ReleaseProfile> ReleaseProfiles => Set<ReleaseProfile>();
    public DbSet<EpisodeNumberingRule> EpisodeNumberingRules => Set<EpisodeNumberingRule>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MediaItem>(e =>
        {
              e.HasIndex(x => new { x.TmdbId, x.Type }).IsUnique();
            e.Property(x => x.Title).HasMaxLength(500);
            e.HasMany(x => x.Seasons)
                .WithOne(x => x.MediaItem!)
                .HasForeignKey(x => x.MediaItemId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.EpisodeNumberingRules)
                .WithOne(x => x.MediaItem!)
                .HasForeignKey(x => x.MediaItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EpisodeNumberingRule>(e =>
        {
            e.HasIndex(x => new { x.MediaItemId, x.ReleaseSeasonNumber }).IsUnique();
        });

        b.Entity<Season>()
            .HasMany(x => x.Episodes)
            .WithOne(x => x.Season!)
            .HasForeignKey(x => x.SeasonId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<User>().HasIndex(x => x.Email).IsUnique();

        b.Entity<PipelineJob>(e =>
        {
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });

        // Les listes de termes des profils sont stockées en jsonb (support natif Npgsql).
        b.Entity<ReleaseProfile>(e =>
        {
            e.Property(x => x.AllowedResolutions).HasColumnType("jsonb");
            e.Property(x => x.AllowedVideoCodecs).HasColumnType("jsonb");
            e.Property(x => x.RejectedVideoCodecs).HasColumnType("jsonb");
            e.Property(x => x.AllowedSources).HasColumnType("jsonb");
            e.Property(x => x.RejectedTerms).HasColumnType("jsonb");
        });
    }
}
