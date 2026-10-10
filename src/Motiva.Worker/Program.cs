using Microsoft.Extensions.Hosting;
using Motiva.Infrastructure;
using Motiva.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMotivaInfrastructure(builder.Configuration);
builder.Services.AddScoped<Motiva.Application.UseCases.LeaderboardUseCases>();
builder.Services.AddHostedService<MotivaWorkerService>();
var host = builder.Build();
await host.RunAsync();
