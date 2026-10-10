namespace Motiva.Domain;

/// <summary>Операция (B28/D201). Не удаляется.</summary>
public sealed class Operation
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public string InitiatorKey { get; set; } = string.Empty;
    public OperationKind Kind { get; set; }
    public string SourceNo { get; set; } = string.Empty;
    public string? Basis { get; set; }
    public long? EmployeeId { get; set; }
    public OperationResult Result { get; set; }
    public string? RejectReason { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public long? CampaignId { get; set; }
    public long? PurchaseSystemId { get; set; }
    public long? OriginalOperationId { get; set; }
    public CancellationState CancellationState { get; set; } = CancellationState.None;
    public List<OperationLine> Lines { get; set; } = new();
}

/// <summary>Состояние отмены исходной операции (D202).</summary>
public enum CancellationState
{
    None,
    Posted,
}

/// <summary>Позиция пакета операции (B20/D202).</summary>
public sealed class OperationLine
{
    public long OperationId { get; set; }
    public long ResourceId { get; set; }
    public int Amount { get; set; }
}

/// <summary>Сохранённый запрос операции для бессрочного повтора (D201).</summary>
public sealed class OperationRequest
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public string InitiatorKey { get; set; } = string.Empty;
    public OperationKind Kind { get; set; }
    public string SourceNo { get; set; } = string.Empty;
    public string CanonicalPayload { get; set; } = string.Empty;
    public OperationResult Result { get; set; }
    public int HttpStatus { get; set; }
    public string ResponseView { get; set; } = string.Empty;
    public long? OperationId { get; set; }
}

/// <summary>Движение кошелька (B28). Объясняет каждый баланс.</summary>
public sealed class WalletMovement
{
    public long Id { get; set; }
    public long WalletId { get; set; }
    public long OperationId { get; set; }
    public long ResourceId { get; set; }
    public long Delta { get; set; }
    public long BalanceAfter { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public OperationKind Kind { get; set; }
    public long? CampaignId { get; set; }
    public long? OriginalOperationId { get; set; }
}
