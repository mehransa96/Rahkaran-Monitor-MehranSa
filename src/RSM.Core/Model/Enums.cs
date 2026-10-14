namespace RSM.Core.Model;

/// <summary>Architectural layer a collector, capability or finding belongs to.</summary>
public enum Layer
{
    Sql,
    Database,
    Os,
    Storage,
    Iis,
    Arr,
    Redis,
    Ssrs,
    Services,
    Events,
    RabbitMq,
    DotNet,
    Rahkaran,
    Rsm,
}

public enum Severity
{
    Ok = 0,
    Info = 1,
    Warning = 2,
    Critical = 3,
}

/// <summary>Roles a server node can play in a Rahkaran site. One machine may have several.</summary>
[Flags]
public enum NodeRoles
{
    None = 0,
    Data = 1,
    Report = 2,
    App = 4,
    Arr = 8,
    Redis = 16,
    Lock = 32,      // CodeMeter licence server
    Workflow = 64,  // RabbitMQ
}

public enum CollectorStatusKind
{
    Ok,
    Partial,
    Unavailable,
    Error,
    Skipped,
}

public enum CollectorCost
{
    Cheap,
    Medium,
    /// <summary>On-demand only, with warning and time estimate.</summary>
    Heavy,
}

/// <summary>Where a threshold or recommendation comes from.</summary>
public enum SourceKind
{
    /// <summary>Systemgroup ستاد فنی documents (e.g. SQL Server Tuning V.3).</summary>
    StadFanni,
    /// <summary>Systemgroup knowledge-base articles.</summary>
    KnowledgeBase,
    /// <summary>Rahkaran architecture facts stated in the spec.</summary>
    Architecture,
    /// <summary>Hardware vendor guide (HPE).</summary>
    Vendor,
    /// <summary>General industry guidance — must be labelled "confirm with ستاد فنی".</summary>
    General,
}
