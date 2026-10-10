using System.Net;
using System.Net.Http.Headers;
using MediaHub.Core.Abstractions;
using MediaHub.Infrastructure.Clients;
using MediaHub.Infrastructure.Data;
using MediaHub.Infrastructure.Encoding;
using MediaHub.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddMemoryCache();

        // Options
        services.Configure<TmdbOptions>(config.GetSection(TmdbOptions.Section));
        services.Configure<TorznabOptions>(config.GetSection(TorznabOptions.Section));
        services.Configure<QBittorrentOptions>(config.GetSection(QBittorrentOptions.Section));
        services.Configure<MegaOptions>(config.GetSection(MegaOptions.Section));
        services.Configure<HandBrakeOptions>(config.GetSection(HandBrakeOptions.Section));
        services.Configure<FfprobeOptions>(config.GetSection(FfprobeOptions.Section));
        services.Configure<LibraryOptions>(config.GetSection(LibraryOptions.Section));

        // Base de données
        var connectionString = config.GetConnectionString("Default")
            ?? "Host=localhost;Database=mediahub;Username=mediahub;Password=mediahub";
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

        // TMDB
        services.AddHttpClient<ITmdbClient, TmdbClient>((sp, http) =>
        {
            var opt = GetSection<TmdbOptions>(config, TmdbOptions.Section);
            http.BaseAddress = new Uri("https://api.themoviedb.org");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opt.ApiKey);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        // Indexeur Torznab (C411)
        services.AddHttpClient<ITorznabClient, TorznabClient>((sp, http) =>
        {
            var opt = GetSection<TorznabOptions>(config, TorznabOptions.Section);
            http.BaseAddress = new Uri(opt.BaseUrl);
        });

        // qBittorrent (cookie de session partagé)
        services.AddHttpClient<IDownloadClient, QBittorrentClient>((sp, http) =>
        {
            var opt = GetSection<QBittorrentOptions>(config, QBittorrentOptions.Section);
            http.BaseAddress = new Uri(opt.BaseUrl);
            http.DefaultRequestHeaders.Referrer = new Uri(opt.BaseUrl);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer()
        });

        // Stockage cloud & encodeur (process externes)
        services.AddSingleton<ICloudStorage, MegaCmdStorage>();
        services.AddSingleton<IVideoEncoder, HandBrakeEncoder>();

        // Services applicatifs
        services.AddScoped<Services.LibraryService>();
        services.AddScoped<Services.StorageScanService>();
        services.AddScoped<Services.StorageMediaInspector>();
        services.AddScoped<Services.EpisodeSyncService>();

        return services;
    }

    private static T GetSection<T>(IConfiguration config, string section) where T : new()
    {
        var instance = new T();
        config.GetSection(section).Bind(instance);
        return instance;
    }
}
