using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Extensions;
using Serilog;
using Serilog.Context;

const string GatewaySystemName = "ApiGateway";
const string RequestIdHeader = "X-Request-ID";
const string RequestIdItemName = "RequestId";
const string TargetSystemItemName = "TargetSystem";
const string RouteIdItemName = "RouteId";
const string ClusterIdItemName = "ClusterId";
const string DestinationIdItemName = "DestinationId";
const string DestinationAddressItemName = "DestinationAddress";
const string UnknownValue = "unknown";

var sensitiveHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Authorization",
    "Cookie",
    "Proxy-Authorization",
    "Set-Cookie",
    "X-Api-Key"
};

var textContentTypeMarkers = new[]
{
    "json",
    "text",
    "xml",
    "x-www-form-urlencoded",
    "graphql"
};

var sensitiveBodyFieldMarkers = new[]
{
    "password",
    "secret",
    "token",
    "apikey",
    "api_key",
    "authorization"
};

var builder = WebApplication.CreateBuilder(args);
var rateLimitPermitLimit = Math.Max(1, builder.Configuration.GetValue("RateLimiting:PermitLimit", 100));
var rateLimitWindowSeconds = Math.Max(1, builder.Configuration.GetValue("RateLimiting:WindowSeconds", 60));
var logRequestBody = builder.Configuration.GetValue("GatewayLogging:LogRequestBody", true);
var logResponseBody = builder.Configuration.GetValue("GatewayLogging:LogResponseBody", true);
var maxLoggedBodyBytes = Math.Max(0, builder.Configuration.GetValue("GatewayLogging:MaxLoggedBodyBytes", 65_536));

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", GatewaySystemName)
        .Enrich.WithProperty("System", GatewaySystemName);
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
    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? UnknownValue;
    var requestBody = await ReadRequestBodyAsync(context, logRequestBody, maxLoggedBodyBytes);
    Exception? requestException = null;

    context.Items[RequestIdItemName] = requestId;
    context.Request.Headers[RequestIdHeader] = requestId;
    context.Response.OnStarting(() =>
    {
        context.Response.Headers[RequestIdHeader] = requestId;
        return Task.CompletedTask;
    });

    using var requestIdProperty = LogContext.PushProperty("RequestId", requestId);
    using var correlationIdProperty = LogContext.PushProperty("CorrelationId", requestId);
    using var gatewaySystemProperty = LogContext.PushProperty("GatewaySystem", GatewaySystemName);
    using var clientIpProperty = LogContext.PushProperty("ClientIp", clientIp);
    using var rateLimitProperty = LogContext.PushProperty("RateLimitPartition", GetRateLimitPartitionKey(context));

    requestLogger.LogInformation(
        "[GW][01 IN ] id={RequestId} {Method} {Path}{QueryString} client={ClientIp}",
        requestId,
        context.Request.Method,
        context.Request.Path,
        context.Request.QueryString,
        clientIp);

    requestLogger.LogInformation(
        "Gateway request received {@GatewayRequest}",
        new
        {
            EventType = "GatewayRequestReceived",
            RequestId = requestId,
            GatewaySystem = GatewaySystemName,
            Method = context.Request.Method,
            Scheme = context.Request.Scheme,
            Host = context.Request.Host.ToString(),
            Path = context.Request.Path.ToString(),
            QueryString = context.Request.QueryString.ToString(),
            Url = context.Request.GetDisplayUrl(),
            ClientIp = clientIp,
            UserId = GetUserId(context),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            ForwardedFor = context.Request.Headers["X-Forwarded-For"].ToString(),
            ContentType = context.Request.ContentType,
            ContentLength = context.Request.ContentLength,
            Headers = GetHeaders(context.Request.Headers),
            Body = requestBody
        });

    var originalResponseBody = context.Response.Body;
    await using var responseBodyCapture = new ResponseBodyCaptureStream(originalResponseBody, maxLoggedBodyBytes);
    context.Response.Body = responseBodyCapture;

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
        context.Response.Body = originalResponseBody;

        var responseBody = BuildResponseBodyLog(context, responseBodyCapture, logResponseBody, maxLoggedBodyBytes);
        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var statusCode = requestException is null
            ? context.Response.StatusCode
            : StatusCodes.Status500InternalServerError;
        var targetSystem = context.Items[TargetSystemItemName] as string ?? GetSystemNameFromPath(context.Request.Path);
        var routeId = context.Items[RouteIdItemName] as string ?? UnknownValue;
        var clusterId = context.Items[ClusterIdItemName] as string ?? UnknownValue;
        var destinationId = context.Items[DestinationIdItemName] as string ?? UnknownValue;
        var destinationAddress = context.Items[DestinationAddressItemName] as string ?? UnknownValue;

        requestLogger.LogInformation(
            "[GW][04 OUT] id={RequestId} system={TargetSystem} route={RouteId} cluster={ClusterId} destination={DestinationId} status={StatusCode} {Method} {Path}{QueryString} duration={ElapsedMs:0.0}ms",
            requestId,
            targetSystem,
            routeId,
            clusterId,
            destinationId,
            statusCode,
            context.Request.Method,
            context.Request.Path,
            context.Request.QueryString,
            elapsedMs);

        requestLogger.LogInformation(
            "Gateway request completed {@GatewayExchange}",
            new
            {
                EventType = "GatewayRequestCompleted",
                RequestId = requestId,
                GatewaySystem = GatewaySystemName,
                TargetSystem = targetSystem,
                RouteId = routeId,
                ClusterId = clusterId,
                DestinationId = destinationId,
                DestinationAddress = destinationAddress,
                Method = context.Request.Method,
                Scheme = context.Request.Scheme,
                Host = context.Request.Host.ToString(),
                Path = context.Request.Path.ToString(),
                QueryString = context.Request.QueryString.ToString(),
                Url = context.Request.GetDisplayUrl(),
                ClientIp = clientIp,
                UserId = GetUserId(context),
                StatusCode = statusCode,
                DurationMs = Math.Round(elapsedMs, 2),
                RequestHeaders = GetHeaders(context.Request.Headers),
                RequestBody = requestBody,
                ResponseHeaders = GetHeaders(context.Response.Headers),
                ResponseBody = responseBody,
                Exception = requestException?.Message
            });
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
        var routeId = reverseProxyFeature.Route?.Config.RouteId ?? UnknownValue;
        var clusterId = reverseProxyFeature.Cluster?.Config.ClusterId ?? UnknownValue;
        var targetSystem = GetRouteMetadataValue(reverseProxyFeature.Route?.Config.Metadata, "System")
            ?? GetSystemNameFromCluster(clusterId);
        var destinations = reverseProxyFeature.AvailableDestinations.Select(destination => new
        {
            destination.DestinationId,
            destination.Model.Config.Address
        }).ToArray();

        context.Items[TargetSystemItemName] = targetSystem;
        context.Items[RouteIdItemName] = routeId;
        context.Items[ClusterIdItemName] = clusterId;

        using var targetSystemProperty = LogContext.PushProperty("TargetSystem", targetSystem);
        using var routeIdProperty = LogContext.PushProperty("RouteId", routeId);
        using var clusterIdProperty = LogContext.PushProperty("ClusterId", clusterId);

        requestLogger.LogInformation(
            "[GW][02 RTE] id={RequestId} system={TargetSystem} route={RouteId} cluster={ClusterId} destinations={@Destinations}",
            requestId,
            targetSystem,
            routeId,
            clusterId,
            destinations);

        requestLogger.LogInformation(
            "[GW][03 FWD] id={RequestId} system={TargetSystem} forwarding {Method} {Path}{QueryString}",
            requestId,
            targetSystem,
            context.Request.Method,
            context.Request.Path,
            context.Request.QueryString);

        await next();

        var proxiedDestination = reverseProxyFeature.ProxiedDestination;
        if (proxiedDestination is not null)
        {
            context.Items[DestinationIdItemName] = proxiedDestination.DestinationId;
            context.Items[DestinationAddressItemName] = proxiedDestination.Model.Config.Address;

            requestLogger.LogInformation(
                "[GW][03 FWD] id={RequestId} system={TargetSystem} destination={DestinationId} address={Address}",
                requestId,
                targetSystem,
                proxiedDestination.DestinationId,
                proxiedDestination.Model.Config.Address);
        }
        else
        {
            requestLogger.LogWarning(
                "[GW][03 FWD] id={RequestId} system={TargetSystem} destination=not-selected",
                requestId,
                targetSystem);
        }
    });
});

app.Run();

async Task<BodyLog> ReadRequestBodyAsync(HttpContext context, bool enabled, int maxBytes)
{
    var request = context.Request;
    if (!enabled)
    {
        return BodyLog.Skipped("Request body logging is disabled.");
    }

    if (request.ContentLength is 0 || (request.ContentLength is null && !request.Headers.ContainsKey("Transfer-Encoding")))
    {
        return BodyLog.Empty(request.ContentType, request.ContentLength);
    }

    if (!IsTextContentType(request.ContentType))
    {
        return BodyLog.Skipped("Request body content type is not text.", request.ContentType, request.ContentLength);
    }

    try
    {
        request.EnableBuffering();
        var bodyLog = await ReadBodyFromStreamAsync(request.Body, request.ContentType, request.ContentLength, maxBytes, context.RequestAborted);
        request.Body.Position = 0;

        return bodyLog;
    }
    catch (Exception exception) when (exception is IOException or OperationCanceledException)
    {
        return BodyLog.Skipped($"Request body could not be read: {exception.Message}", request.ContentType, request.ContentLength);
    }
}

BodyLog BuildResponseBodyLog(HttpContext context, ResponseBodyCaptureStream responseBodyCapture, bool enabled, int maxBytes)
{
    if (!enabled)
    {
        return BodyLog.Skipped("Response body logging is disabled.", context.Response.ContentType, responseBodyCapture.TotalBytesWritten);
    }

    if (responseBodyCapture.TotalBytesWritten is 0)
    {
        return BodyLog.Empty(context.Response.ContentType, 0);
    }

    if (!IsTextContentType(context.Response.ContentType))
    {
        return BodyLog.Skipped("Response body content type is not text.", context.Response.ContentType, responseBodyCapture.TotalBytesWritten);
    }

    if (maxBytes is 0)
    {
        return BodyLog.Skipped("Body logging byte limit is set to 0.", context.Response.ContentType, responseBodyCapture.TotalBytesWritten);
    }

    var bytes = responseBodyCapture.GetCapturedBytes();
    var isTruncated = responseBodyCapture.TotalBytesWritten > maxBytes;
    if (bytes.Length > maxBytes)
    {
        Array.Resize(ref bytes, maxBytes);
    }

    var text = Encoding.UTF8.GetString(bytes);
    return BodyLog.FromText(
        RedactBody(context.Response.ContentType, text),
        context.Response.ContentType,
        responseBodyCapture.TotalBytesWritten,
        isTruncated,
        bytes.Length);
}

async Task<BodyLog> ReadBodyFromStreamAsync(Stream body, string? contentType, long? contentLength, int maxBytes, CancellationToken cancellationToken)
{
    if (maxBytes is 0)
    {
        return BodyLog.Skipped("Body logging byte limit is set to 0.", contentType, contentLength);
    }

    var limit = maxBytes + 1;
    var buffer = new byte[Math.Min(8_192, limit)];
    await using var capturedBody = new MemoryStream();

    while (capturedBody.Length < limit)
    {
        var bytesToRead = Math.Min(buffer.Length, limit - (int)capturedBody.Length);
        var bytesRead = await body.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);

        if (bytesRead is 0)
        {
            break;
        }

        capturedBody.Write(buffer, 0, bytesRead);
    }

    var isTruncated = capturedBody.Length > maxBytes;
    var bytes = capturedBody.ToArray();
    if (isTruncated)
    {
        Array.Resize(ref bytes, maxBytes);
    }

    var text = Encoding.UTF8.GetString(bytes);
    return BodyLog.FromText(
        RedactBody(contentType, text),
        contentType,
        contentLength,
        isTruncated,
        bytes.Length);
}

Dictionary<string, string[]> GetHeaders(IHeaderDictionary headers)
{
    return headers.ToDictionary(
        header => header.Key,
        header => sensitiveHeaderNames.Contains(header.Key)
            ? new[] { "***REDACTED***" }
            : header.Value.Select(value => value ?? string.Empty).ToArray(),
        StringComparer.OrdinalIgnoreCase);
}

string GetOrCreateRequestId(HttpContext context)
{
    var requestId = context.Request.Headers[RequestIdHeader].FirstOrDefault();

    return string.IsNullOrWhiteSpace(requestId)
        ? context.TraceIdentifier
        : requestId;
}

string? GetUserId(HttpContext context)
{
    return context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.User.FindFirstValue("sub")
        ?? context.User.Identity?.Name;
}

string GetRateLimitPartitionKey(HttpContext context)
{
    var userId = GetUserId(context);

    if (!string.IsNullOrWhiteSpace(userId))
    {
        return $"user:{userId}";
    }

    return $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? UnknownValue}";
}

string GetSystemNameFromPath(PathString path)
{
    if (path.StartsWithSegments("/agents", StringComparison.OrdinalIgnoreCase))
    {
        return "AgentsService";
    }

    if (path.StartsWithSegments("/missions", StringComparison.OrdinalIgnoreCase))
    {
        return "MissionsService";
    }

    return UnknownValue;
}

string GetSystemNameFromCluster(string clusterId)
{
    return clusterId switch
    {
        "agents-cluster" => "AgentsService",
        "missions-cluster" => "MissionsService",
        _ => UnknownValue
    };
}

string? GetRouteMetadataValue(IReadOnlyDictionary<string, string>? metadata, string key)
{
    return metadata is not null && metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : null;
}

bool IsTextContentType(string? contentType)
{
    return !string.IsNullOrWhiteSpace(contentType)
        && textContentTypeMarkers.Any(marker => contentType.Contains(marker, StringComparison.OrdinalIgnoreCase));
}

string RedactBody(string? contentType, string body)
{
    if (string.IsNullOrWhiteSpace(body)
        || string.IsNullOrWhiteSpace(contentType)
        || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
    {
        return body;
    }

    try
    {
        using var document = JsonDocument.Parse(body);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        WriteRedactedJson(writer, document.RootElement);
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }
    catch (JsonException)
    {
        return body;
    }
}

void WriteRedactedJson(Utf8JsonWriter writer, JsonElement element)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (IsSensitiveBodyField(property.Name))
                {
                    writer.WriteStringValue("***REDACTED***");
                }
                else
                {
                    WriteRedactedJson(writer, property.Value);
                }
            }
            writer.WriteEndObject();
            break;
        case JsonValueKind.Array:
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                WriteRedactedJson(writer, item);
            }
            writer.WriteEndArray();
            break;
        default:
            element.WriteTo(writer);
            break;
    }
}

bool IsSensitiveBodyField(string fieldName)
{
    return sensitiveBodyFieldMarkers.Any(marker => fieldName.Contains(marker, StringComparison.OrdinalIgnoreCase));
}

record BodyLog(
    bool Logged,
    string? Text,
    string? ContentType,
    long? ContentLength,
    bool Truncated,
    int LoggedBytes,
    string? SkippedReason)
{
    public static BodyLog FromText(string text, string? contentType, long? contentLength, bool truncated, int loggedBytes) =>
        new(true, text, contentType, contentLength, truncated, loggedBytes, null);

    public static BodyLog Empty(string? contentType, long? contentLength) =>
        new(false, null, contentType, contentLength, false, 0, "Body is empty.");

    public static BodyLog Skipped(string reason, string? contentType = null, long? contentLength = null) =>
        new(false, null, contentType, contentLength, false, 0, reason);
}

sealed class ResponseBodyCaptureStream : Stream
{
    private readonly Stream inner;
    private readonly MemoryStream capture = new();
    private readonly int captureLimit;

    public ResponseBodyCaptureStream(Stream inner, int maxLoggedBodyBytes)
    {
        this.inner = inner;
        captureLimit = maxLoggedBodyBytes <= 0
            ? 0
            : maxLoggedBodyBytes == int.MaxValue
                ? int.MaxValue
                : maxLoggedBodyBytes + 1;
    }

    public long TotalBytesWritten { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public byte[] GetCapturedBytes() => capture.ToArray();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        Capture(buffer.AsSpan(offset, count));
        TotalBytesWritten += count;
        inner.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Capture(buffer.AsSpan(offset, count));
        TotalBytesWritten += count;
        return inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Capture(buffer.Span);
        TotalBytesWritten += buffer.Length;
        return inner.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            capture.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Capture(ReadOnlySpan<byte> buffer)
    {
        if (captureLimit is 0 || capture.Length >= captureLimit)
        {
            return;
        }

        var bytesToCapture = Math.Min(buffer.Length, captureLimit - (int)capture.Length);
        capture.Write(buffer[..bytesToCapture]);
    }
}
