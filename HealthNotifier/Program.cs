using HealthNotifier;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

builder.Services.Configure<HealthNotificationOptions>(
    builder.Configuration.GetSection(HealthNotificationOptions.SectionName));

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptionsMonitor<HealthNotificationOptions>>().CurrentValue;
    var handler = new HttpClientHandler();

    if (options.AllowInvalidCertificates)
    {
        handler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    }

    return new HttpClient(handler);
});

builder.Services.AddSingleton<EmailNotificationSender>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
