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
    public DbSet<WatchSettings> WatchSettings => Set<WatchSettings>();
    public DbSet<MediaWatch> MediaWatches => Set<MediaWatch>();
    public DbSet<ReleaseObservation> ReleaseObservations => Set<ReleaseObservation>();
    public DbSet<WatchScan> WatchScans => Set<WatchScan>();
    public DbSet<WatchScanItem> WatchScanItems => Set<WatchScanItem>();
    public DbSet<TelegramTrackSelection> TelegramTrackSelections => Set<TelegramTrackSelection>();
    public DbSet<StockInspection> StockInspections => Set<StockInspection>();
    public DbSet<StockFileInspection> StockFileInspections => Set<StockFileInspection>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<StockInspection>().HasKey(value => value.MediaItemId);
        b.Entity<StockInspection>().HasOne(value => value.MediaItem).WithOne()
            .HasForeignKey<StockInspection>(value => value.MediaItemId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<StockFileInspection>().HasOne(value => value.MediaItem).WithMany()
            .HasForeignKey(value => value.MediaItemId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<StockFileInspection>().HasIndex(value => new { value.MediaItemId, value.Path }).IsUnique();
        b.Entity<TelegramTrackSelection>().HasData(new TelegramTrackSelection { SessionId = Guid.Empty });
        b.Entity<WatchScan>().HasMany(value => value.Items).WithOne(value => value.WatchScan)
            .HasForeignKey(value => value.WatchScanId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WatchScan>().HasIndex(value => value.CreatedAt);
        b.Entity<WatchScanItem>().HasIndex(value => new { value.WatchScanId, value.MediaItemId }).IsUnique();
        b.Entity<WatchSettings>().HasData(new WatchSettings());
        b.Entity<MediaWatch>(e =>
        {
            e.HasKey(x => x.MediaItemId);
            e.HasOne(x => x.MediaItem).WithOne().HasForeignKey<MediaWatch>(x => x.MediaItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.NextCheckAt);
        });
        b.Entity<ReleaseObservation>(e =>
        {
            e.HasOne(x => x.MediaItem).WithMany().HasForeignKey(x => x.MediaItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.MediaItemId, x.Fingerprint }).IsUnique();
            e.HasIndex(x => new { x.Decision, x.NextNotificationAt });
            e.HasIndex(x => x.JobId).IsUnique();
            e.Property(x => x.Fingerprint).HasMaxLength(64);
        });

        b.Entity<MediaItem>(e =>
        {
              e.HasIndex(x => new { x.TmdbId, x.Type }).IsUnique();
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.WatchEnabled).HasDefaultValue(true);
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
