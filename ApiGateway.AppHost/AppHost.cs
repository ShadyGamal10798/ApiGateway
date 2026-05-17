using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = DistributedApplication.CreateBuilder(args);

builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
});

var gateway = builder.AddProject<Projects.ApiGateway>("api-gateway")
    .WithHttpHealthCheck("/health");

var agents = builder.AddProject<Projects.AgentsService>("agents-service")
    .WithHttpHealthCheck("/health");

var missions = builder.AddProject<Projects.MissionsService>("missions-service")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.HealthNotifier>("health-notifier")
    .WithEnvironment("HealthNotifications__AllowInvalidCertificates", "true")
    .WithEnvironment("HealthNotifications__Targets__0__Url", ReferenceExpression.Create($"{gateway.GetEndpoint("https")}/health"))
    .WithEnvironment("HealthNotifications__Targets__1__Url", ReferenceExpression.Create($"{agents.GetEndpoint("https")}/health"))
    .WithEnvironment("HealthNotifications__Targets__2__Url", ReferenceExpression.Create($"{missions.GetEndpoint("https")}/health"));

builder.Build().Run();
