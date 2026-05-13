using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;

const string RequestIdHeader = "X-Request-ID";
const string RequestIdItemName = "RequestId";

var builder = WebApplication.CreateBuilder(args);
var rateLimitPermitLimit = Math.Max(1, builder.Configuration.GetValue("RateLimiting:PermitLimit", 100));
var rateLimitWindowSeconds = Math.Max(1, builder.Configuration.GetValue("RateLimiting:WindowSeconds", 60));

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: GetRateLimitPartitionKey(context),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateLimitPermitLimit,
                Window = TimeSpan.FromSeconds(rateLimitWindowSeconds),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));

    options.OnRejected = (context, cancellationToken) =>
    {
        var response = context.HttpContext.Response;
        response.ContentType = "text/plain";

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            response.Headers["Retry-After"] =
                Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        return new ValueTask(response.WriteAsync("Rate limit exceeded. Try again later.", cancellationToken));
    };
});

var app = builder.Build();
var requestLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ApiGateway.Requests");

app.Use(async (context, next) =>
{
    var startedAt = Stopwatch.GetTimestamp();
    var requestId = GetOrCreateRequestId(context);
    Exception? requestException = null;

    context.Items[RequestIdItemName] = requestId;
    context.Request.Headers[RequestIdHeader] = requestId;
    context.Response.OnStarting(() =>
    {
        context.Response.Headers[RequestIdHeader] = requestId;
        return Task.CompletedTask;
    });

    requestLogger.LogInformation(
        "[GW][01 IN ] id={RequestId} {Method} {Path}{QueryString} client={RemoteIp}",
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
            "[GW][ERR   ] id={RequestId} {Method} {Path}{QueryString} failed: {ErrorMessage}",
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
            "[GW][04 OUT] id={RequestId} status={StatusCode} {Method} {Path}{QueryString} duration={ElapsedMs:0.0}ms",
            requestId,
            statusCode,
            context.Request.Method,
            context.Request.Path,
            context.Request.QueryString,
            elapsedMs);
    }
});

app.UseHttpsRedirection();
app.UseRateLimiter();

app.MapReverseProxy(proxyPipeline =>
{
    proxyPipeline.Use(async (context, next) =>
    {
        var requestId = context.Items[RequestIdItemName] as string ?? context.TraceIdentifier;
        var reverseProxyFeature = context.GetReverseProxyFeature();
        var routeId = reverseProxyFeature.Route?.Config.RouteId ?? "unknown-route";
        var clusterId = reverseProxyFeature.Cluster?.Config.ClusterId ?? "unknown-cluster";
        var destinations = string.Join(", ", reverseProxyFeature.AvailableDestinations.Select(
            destination => $"{destination.DestinationId}={destination.Model.Config.Address}"));

        requestLogger.LogInformation(
            "[GW][02 RTE] id={RequestId} route={RouteId} cluster={ClusterId} destinations=[{Destinations}]",
            requestId,
            routeId,
            clusterId,
            destinations);

        requestLogger.LogInformation(
            "[GW][03 FWD] id={RequestId} forwarding {Method} {Path}{QueryString}",
            requestId,
            context.Request.Method,
            context.Request.Path,
            context.Request.QueryString);

        await next();

        var proxiedDestination = reverseProxyFeature.ProxiedDestination;
        if (proxiedDestination is not null)
        {
            requestLogger.LogInformation(
                "[GW][03 FWD] id={RequestId} destination={DestinationId} address={Address}",
                requestId,
                proxiedDestination.DestinationId,
                proxiedDestination.Model.Config.Address);
        }
        else
        {
            requestLogger.LogWarning(
                "[GW][03 FWD] id={RequestId} destination=not-selected",
                requestId);
        }
    });
});

app.Run();

string GetOrCreateRequestId(HttpContext context)
{
    var requestId = context.Request.Headers[RequestIdHeader].FirstOrDefault();

    return string.IsNullOrWhiteSpace(requestId)
        ? context.TraceIdentifier
        : requestId;
}

string GetRateLimitPartitionKey(HttpContext context)
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.User.FindFirstValue("sub")
        ?? context.User.Identity?.Name;

    if (!string.IsNullOrWhiteSpace(userId))
    {
        return $"user:{userId}";
    }

    return $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}
