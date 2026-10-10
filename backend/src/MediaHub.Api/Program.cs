using MediaHub.Infrastructure;
using MediaHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

const string FrontendCors = "frontend";

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCors, policy => policy
        .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
            ?? ["http://localhost:5173"])
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    StartQBittorrentIfNeeded(builder.Configuration, app.Logger);
    app.MapOpenApi();
}

if (builder.Configuration.GetValue("Database:ApplyMigrationsOnStartup", app.Environment.IsDevelopment()))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await DbSeeder.SeedAsync(db);
}

app.UseCors(FrontendCors);
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.Run();

static void StartQBittorrentIfNeeded(IConfiguration configuration, ILogger logger)
{
    if (!configuration.GetValue("QBittorrent:LaunchOnStartup", false) || !OperatingSystem.IsWindows())
        return;

    if (Process.GetProcessesByName("qbittorrent").Length > 0)
        return;

    var executablePath = configuration["QBittorrent:ExecutablePath"];
    if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
    {
        logger.LogWarning("qBittorrent n'a pas été démarré : exécutable introuvable.");
        return;
    }

    try
    {
        Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true });
        logger.LogInformation("Démarrage de qBittorrent.");
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Impossible de démarrer qBittorrent.");
    }
}
