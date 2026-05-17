namespace HealthNotifier;

using Microsoft.Extensions.Options;

public sealed class Worker(
    HttpClient httpClient,
    EmailNotificationSender emailSender,
    IOptionsMonitor<HealthNotificationOptions> optionsMonitor,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly Dictionary<string, HealthTargetState> targetStates = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Health notifier started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = optionsMonitor.CurrentValue;
            var enabledTargets = options.Targets
                .Where(target => target.Enabled && !string.IsNullOrWhiteSpace(target.Name) && !string.IsNullOrWhiteSpace(target.Url))
                .ToArray();

            if (enabledTargets.Length is 0)
            {
                logger.LogWarning("No enabled health check targets are configured.");
            }

            foreach (var target in enabledTargets)
            {
                var result = await CheckTargetAsync(target, options, stoppingToken);
                await HandleStateTransitionAsync(target, result, options, stoppingToken);
            }

            var interval = TimeSpan.FromSeconds(Math.Max(5, options.CheckIntervalSeconds));
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task<HealthCheckResult> CheckTargetAsync(
        HealthCheckTarget target,
        HealthNotificationOptions options,
        CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, target.TimeoutSeconds ?? options.DefaultTimeoutSeconds));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var response = await httpClient.GetAsync(
                target.Url,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);

            var expectedStatusCode = target.ExpectedStatusCode ?? 200;
            var isHealthy = (int)response.StatusCode == expectedStatusCode;

            logger.LogInformation(
                "Health check {TargetName} returned {StatusCode}. Healthy={IsHealthy}",
                target.Name,
                (int)response.StatusCode,
                isHealthy);

            return new HealthCheckResult(
                target.Name,
                target.Url,
                isHealthy,
                (int)response.StatusCode,
                null,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Health check {TargetName} failed for {TargetUrl}",
                target.Name,
                target.Url);

            return new HealthCheckResult(
                target.Name,
                target.Url,
                false,
                null,
                exception.Message,
                DateTimeOffset.UtcNow);
        }
    }

    private async Task HandleStateTransitionAsync(
        HealthCheckTarget target,
        HealthCheckResult result,
        HealthNotificationOptions options,
        CancellationToken stoppingToken)
    {
        targetStates.TryGetValue(target.Name, out var previousState);

        if (!result.IsHealthy && previousState?.IsHealthy is not false)
        {
            await SendAlertAsync(target, result, "DOWN", options, stoppingToken);
        }
        else if (result.IsHealthy && previousState?.IsHealthy is false && options.NotifyOnRecovery)
        {
            await SendAlertAsync(target, result, "RECOVERED", options, stoppingToken);
        }

        targetStates[target.Name] = new HealthTargetState(result.IsHealthy, result.CheckedAtUtc);
    }

    private async Task SendAlertAsync(
        HealthCheckTarget target,
        HealthCheckResult result,
        string state,
        HealthNotificationOptions options,
        CancellationToken stoppingToken)
    {
        var subject = $"[Health Alert] {target.Name} is {state}";
        var body = $"""
        Service: {target.Name}
        State: {state}
        Url: {target.Url}
        Checked at UTC: {result.CheckedAtUtc:O}
        Status code: {result.StatusCode?.ToString() ?? "N/A"}
        Error: {result.Error ?? "N/A"}
        """;

        try
        {
            await emailSender.SendAsync(subject, body, options.Email, stoppingToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to send {State} notification for {TargetName}",
                state,
                target.Name);
        }
    }

    private sealed record HealthCheckResult(
        string TargetName,
        string TargetUrl,
        bool IsHealthy,
        int? StatusCode,
        string? Error,
        DateTimeOffset CheckedAtUtc);

    private sealed record HealthTargetState(bool IsHealthy, DateTimeOffset CheckedAtUtc);
}
