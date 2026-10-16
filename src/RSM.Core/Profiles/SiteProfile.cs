using RSM.Core.Model;

namespace RSM.Core.Profiles;

/// <summary>One customer site. Persisted as human-readable JSON; secrets are never part of it.</summary>
public sealed record SiteProfile
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Notes { get; init; }

    public IReadOnlyList<NodeProfile> Nodes { get; init; } = [];

    public SiteSettings Settings { get; init; } = new();

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

/// <summary>Site-level settings. Values marked unverified are product-specific and empty until the team confirms them.</summary>
public sealed record SiteSettings
{
    /// <summary>Main Rahkaran database name(s); empty = auto-detect.</summary>
    public IReadOnlyList<string> RahkaranDatabases { get; init; } = [];

    /// <summary>Unverified: Windows service name pattern of the attendance (تردد) module.</summary>
    public string? AttendanceServicePattern { get; init; }

    /// <summary>Unverified: service name pattern of the Systemgroup proactive monitor (spec §9.7 names "SGProactiveMonitor").</summary>
    public string? ProactiveMonitorServicePattern { get; init; } = "SGProactiveMonitor*";

    /// <summary>Antivirus service name patterns shown on the Services page (Defender by default; add the site's product).</summary>
    public IReadOnlyList<string> AntivirusServicePatterns { get; init; } = ["WinDefend"];

    /// <summary>Unverified: SQL Agent job name patterns used by the ستاد فنی database tool.</summary>
    public IReadOnlyList<string> StadFanniToolJobPatterns { get; init; } = [];

    /// <summary>Working days (0 = Saturday … 6 = Friday) for the log-backup interval check.</summary>
    public IReadOnlyList<int> WorkingDays { get; init; } = [0, 1, 2, 3, 4, 5];

    public TimeOnly WorkdayStart { get; init; } = new(8, 0);

    public TimeOnly WorkdayEnd { get; init; } = new(17, 0);

    /// <summary>Thursday (index 5) ends earlier.</summary>
    public TimeOnly ThursdayEnd { get; init; } = new(13, 0);

    /// <summary>
    /// Unverified: a file inside the Rahkaran application folder (relative path, e.g. a main assembly) whose content
    /// fingerprint identifies the deployed build on each App node (RK-001). Empty = compare only app path segments.
    /// </summary>
    public string? RahkaranVersionFile { get; init; }

    /// <summary>
    /// Optional, off by default (spec §9.4, PLAN Q8): read EventPipe counters (GC, thread pool, exceptions, requests)
    /// of the local w3wp/dotnet processes. The UI warns before this is switched on; no dumps, no other diagnostics.
    /// </summary>
    public bool DotNetRuntimeCounters { get; init; }

    /// <summary>Data size from which a database counts as "large" (log 10 GB guideline; diff + weekly full allowed).</summary>
    public int LargeDatabaseThresholdGb { get; init; } = 200;
}

public sealed record NodeProfile
{
    public required string Id { get; init; }

    /// <summary>Display name (masked in shared reports).</summary>
    public required string Name { get; init; }

    /// <summary>Host name or IP used for Windows/IIS/file access.</summary>
    public required string Host { get; init; }

    public NodeRoles Roles { get; init; }

    public SqlEndpoint? Sql { get; init; }

    public RedisEndpoint? Redis { get; init; }

    public RabbitEndpoint? RabbitMq { get; init; }

    public WindowsAccess Windows { get; init; } = new();
}

public enum SqlAuthKind
{
    Sql,
    Windows,
}

public enum SqlEncryptMode
{
    Mandatory,
    Optional,
    Strict,
}

public sealed record SqlEndpoint
{
    /// <summary>Server[\instance][,port] as accepted by SqlClient "Data Source".</summary>
    public required string Server { get; init; }

    public SqlAuthKind Auth { get; init; } = SqlAuthKind.Sql;

    public string? UserName { get; init; }

    public SqlEncryptMode Encrypt { get; init; } = SqlEncryptMode.Mandatory;

    /// <summary>Accept self-signed certificates. Shown with a warning in the UI.</summary>
    public bool TrustServerCertificate { get; init; }

    public bool RememberPassword { get; init; } = true;

    public int CommandTimeoutSeconds { get; init; } = 10;

    public int MaxConcurrentQueries { get; init; } = 2;
}

public sealed record RedisEndpoint
{
    public required string Host { get; init; }

    public int Port { get; init; } = 6379;

    public bool HasPassword { get; init; }

    public bool RememberPassword { get; init; } = true;
}

public sealed record RabbitEndpoint
{
    /// <summary>Management API base URL, e.g. http://host:15672/. Off unless set.</summary>
    public required Uri ManagementUrl { get; init; }

    public string? UserName { get; init; }

    public bool RememberPassword { get; init; } = true;
}

public sealed record WindowsAccess
{
    /// <summary>Optional DOMAIN\user for remote CIM / event logs / admin share. Empty = current user.</summary>
    public string? UserName { get; init; }

    public bool RememberPassword { get; init; } = true;
}

/// <summary>Identifies one secret belonging to a node.</summary>
public enum SecretKind
{
    SqlPassword,
    RedisPassword,
    RabbitPassword,
    WindowsPassword,
}
