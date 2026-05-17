namespace HealthNotifier;

public sealed class HealthNotificationOptions
{
    public const string SectionName = "HealthNotifications";

    public int CheckIntervalSeconds { get; set; } = 10;

    public int DefaultTimeoutSeconds { get; set; } = 5;

    public bool NotifyOnRecovery { get; set; } = true;

    public bool AllowInvalidCertificates { get; set; }

    public List<HealthCheckTarget> Targets { get; set; } = [];

    public EmailNotificationOptions Email { get; set; } = new();
}

public sealed class HealthCheckTarget
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public int? TimeoutSeconds { get; set; }

    public int? ExpectedStatusCode { get; set; } = 200;
}

public sealed class EmailNotificationOptions
{
    public bool Enabled { get; set; }

    public string From { get; set; } = string.Empty;

    public List<string> Recipients { get; set; } = [];

    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}
