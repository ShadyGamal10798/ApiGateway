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
var requestLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MissionsService.Requests");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");
app.MapGet("/alive", () => Results.Ok(new
{
    Status = "Alive",
    System = "MissionsService",
    CheckedAtUtc = DateTimeOffset.UtcNow
}));

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
        "[MISSIONS][IN ] id={RequestId} {Method} {Path}{QueryString} client={RemoteIp}",
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
            "[MISSIONS][ERR] id={RequestId} {Method} {Path}{QueryString} failed: {ErrorMessage}",
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
            "[MISSIONS][OUT] id={RequestId} status={StatusCode} {Method} {Path}{QueryString} duration={ElapsedMs:0.0}ms",
            requestId,
            statusCode,
            context.Request.Method,
            context.Request.Path,
            context.Request.QueryString,
            elapsedMs);
    }
});

var missions = new List<Mission>
{
    new("M-2024-001", "Operation Nile",  "Cairo",   "High",     "InProgress", "007", "2024-12-31"),
    new("M-2024-002", "Silent Storm",    "London",  "Medium",   "InProgress", "008", "2024-11-15"),
    new("M-2024-003", "Digital Phantom", "Berlin",  "Critical", "InProgress", "009", "2024-12-01"),
    new("M-2024-004", "Desert Wind",     "Dubai",   "Low",      "Completed",  "013", "2024-10-30"),
    new("M-2024-005", "Black Tide",      "Tokyo",   "High",     "Pending",    "011", "2025-01-15"),
    new("M-2024-006", "Lost Echo",       "Unknown", "Critical", "Failed",     "012", "2024-09-30"),
};

app.MapGet("/missions", () => missions);

app.MapGet("/missions/{id}", (string id) =>
{
    var mission = missions.FirstOrDefault(m => m.Id == id);
    return mission is null ? Results.NotFound() : Results.Ok(mission);
});

app.MapGet("/missions/in-progress", () =>
    missions.Where(m => m.Status == "InProgress"));

app.MapGet("/missions/by-agent/{agentId}", (string agentId) =>
    missions.Where(m => m.AssignedAgentId == agentId));

app.MapGet("/missions/by-difficulty/{difficulty}", (string difficulty) =>
    missions.Where(m => m.Difficulty.Equals(difficulty, StringComparison.OrdinalIgnoreCase)));

app.Run();

string GetOrCreateRequestId(HttpContext context)
{
    var requestId = context.Request.Headers[RequestIdHeader].FirstOrDefault();

    return string.IsNullOrWhiteSpace(requestId)
        ? context.TraceIdentifier
        : requestId;
}

record Mission(
    string Id,
    string Name,
    string TargetCity,
    string Difficulty,
    string Status,
    string AssignedAgentId,
    string Deadline
);
