using MediaHub.Infrastructure;
using MediaHub.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<PipelineOrchestrator>();
builder.Services.AddHostedService<PipelineWorker>();

var host = builder.Build();
host.Run();
