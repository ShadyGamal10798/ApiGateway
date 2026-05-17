using System.Diagnostics;

const string RequestIdHeader = "X-Request-ID";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();
var requestLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentsService.Requests");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");
app.MapGet("/alive", () => Results.Ok(new
{
    Status = "Alive",
    System = "AgentsService",
    CheckedAtUtc = DateTimeOffset.UtcNow
}));

// بيانات افتراضية (في تطبيق حقيقي دي هتكون في DB)
var agents = new List<Agent>
{
    new("007", "Cobra",   "Infiltration", "Active",  "Cairo",   47),
    new("008", "Viper",   "Sniping",      "Active",  "London",  32),
    new("009", "Falcon",  "Cyber",        "Active",  "Berlin",  28),
    new("010", "Ghost",   "Surveillance", "Retired", "Lisbon",  89),
    new("011", "Tiger",   "Combat",       "Active",  "Tokyo",   15),
    new("012", "Phoenix", "Demolition",   "Missing", "Unknown", 41),
    new("013", "Shadow",  "Stealth",      "Active",  "Dubai",   23),
    new("014", "Storm",   "Infiltration", "Active",  "Cairo",   19),
};

app.Use(async (context, next) =>
{
    var startedAt = Stopwatch.GetTimestamp();
    var requestId = GetOrCreateRequestId(context);
    Exception? requestException = null;

    context.Response.OnStarting(() =>
    {
        context.Response.Headers[RequestIdHeader] = requestId;
        return Task.CompletedTask;
    });

    requestLogger.LogInformation(
        "[AGENTS][IN ] id={RequestId} {Method} {Path}{QueryString} client={RemoteIp}",
        requestId,
        context.Request.Method,
        context.Request.Path,
        context.Request.QueryString,
        context.Connection.RemoteIpAddress);

    try
    {
        await next();
    }
    catch (Exception exception)
    {
        requestException = exception;
        requestLogger.LogError(
            exception,
            "[AGENTS][ERR] id={RequestId} {Method} {Path}{QueryString} failed: {ErrorMessage}",
            requestId,
            context.Request.Method,
            context.Request.Path,
            context.Request.QueryString,
            exception.Message);

        throw;
    }
    finally
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var statusCode = requestException is null
            ? context.Response.StatusCode
            : StatusCodes.Status500InternalServerError;

        requestLogger.LogInformation(
            "[AGENTS][OUT] id={RequestId} status={StatusCode} {Method} {Path}{QueryString} duration={ElapsedMs:0.0}ms",
            requestId,
            statusCode,
            context.Request.Method,
            context.Request.Path,
            context.Request.QueryString,
            elapsedMs);
    }
});

// Endpoints
app.MapGet("/agents", () => agents);

app.MapGet("/agents/{id}", (string id) =>
{
    var agent = agents.FirstOrDefault(a => a.Id == id);
    return agent is null ? Results.NotFound() : Results.Ok(agent);
});

app.MapGet("/agents/active", () =>
    agents.Where(a => a.Status == "Active"));

app.MapGet("/agents/by-specialty/{specialty}", (string specialty) =>
    agents.Where(a => a.Specialty.Equals(specialty, StringComparison.OrdinalIgnoreCase)));

app.MapGet("/agents/by-location/{location}", (string location) =>
    agents.Where(a => a.Location.Equals(location, StringComparison.OrdinalIgnoreCase)));

app.Run();

string GetOrCreateRequestId(HttpContext context)
{
    var requestId = context.Request.Headers[RequestIdHeader].FirstOrDefault();

    return string.IsNullOrWhiteSpace(requestId)
        ? context.TraceIdentifier
        : requestId;
}

record Agent(
    string Id,
    string Codename,
    string Specialty,
    string Status,
    string Location,
    int MissionsCompleted
);
