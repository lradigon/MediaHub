using MediaHub.Infrastructure;
using MediaHub.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<PipelineOrchestrator>();
builder.Services.AddHostedService<PipelineWorker>();
builder.Services.AddHostedService<ReleaseWatchWorker>();
builder.Services.AddHostedService<TelegramWorker>();
builder.Services.AddHostedService<StockInspectionWorker>();

var host = builder.Build();
host.Run();
