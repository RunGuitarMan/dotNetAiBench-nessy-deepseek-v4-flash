namespace Motiva.Domain;

/// <summary>Заказ выписки (B35/B36).</summary>
public sealed class Statement
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public long OwnerActorId { get; set; }
    public string OwnerActorType { get; set; } = string.Empty;
    public StatementScope Scope { get; set; }
    public long? EmployeeId { get; set; }
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public long? ResourceId { get; set; }
    public StatementState State { get; set; } = StatementState.Pending;
    public string? FileRef { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>Транспортный ключ идемпотентности заказа (T05).</summary>
    public string? TransportKey { get; set; }
}

/// <summary>Зафиксированный снимок состава выписки (B36/D208).</summary>
public sealed class StatementData
{
    public long Id { get; set; }
    public long StatementId { get; set; }
    public Guid SnapshotId { get; set; }
    public string MovementIdsJson { get; set; } = "[]";
}

/// <summary>Попытка формирования (D208): владение + lease.</summary>
public sealed class StatementAttempt
{
    public long StatementId { get; set; }
    public string OwnerWorkerId { get; set; } = string.Empty;
    public DateTimeOffset LeaseExpiresAt { get; set; }
}

/// <summary>Краткосрочная ссылка на скачивание (T04/T05).</summary>
public sealed class StatementDownload
{
    public long Id { get; set; }
    public long StatementId { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    /// <summary>Транспортный ключ идемпотентности ссылки (T05).</summary>
    public string? TransportKey { get; set; }
}

/// <summary>Запись аудита прав/настроек/бюджета (B28d).</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public AuditAction Action { get; set; }
    public Dictionary<string, string> Details { get; set; } = new();
    public DateTimeOffset OccurredAt { get; set; }
}

public enum AuditAction
{
    RightsChange,
    SettingsChange,
    BudgetAllocation,
    OwnerChange,
    PermissionChange,
    Correction,
}
