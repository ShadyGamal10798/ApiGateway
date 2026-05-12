using System.Diagnostics;

const string RequestIdHeader = "X-Request-ID";
const string RequestIdItemName = "RequestId";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

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
