namespace Motiva.Domain;

/// <summary>Прогресс (сотрудник, задание, период) (B14). value ≤ goal.</summary>
public sealed class Progress
{
    public long EmployeeId { get; set; }
    public long TaskId { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public int Value { get; set; }
}

/// <summary>Событие приёма прогресса (B16). Уникален по (company, source, sourceEventNo).</summary>
public sealed class ProgressEvent
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public long SourceId { get; set; }
    public string SourceEventNo { get; set; } = string.Empty;
    public long EmployeeId { get; set; }
    public long TaskId { get; set; }
    public int SentIncrease { get; set; }
    public int CreditedIncrease { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public bool Completed { get; set; }
    public long? RewardOperationId { get; set; }
    public string? RewardResult { get; set; }
    public string? RewardReason { get; set; }
}

/// <summary>Завершение (B15): одно на (сотрудник, задание, период).</summary>
public sealed class Completion
{
    public long EmployeeId { get; set; }
    public long TaskId { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
}

/// <summary>Очки стрима за сезон (B29a).</summary>
public sealed class StreamScore
{
    public long EmployeeId { get; set; }
    public long StreamId { get; set; }
    public int Season { get; set; }
    public long Points { get; set; }
}

/// <summary>Выдача достижения (B30): ≤1 на (сотрудник, достижение, сезон).</summary>
public sealed class AchievementGrant
{
    public long EmployeeId { get; set; }
    public long AchievementId { get; set; }
    public int Season { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
}

/// <summary>Счёт участника челленджа (B31). После final неизменяем (B33).</summary>
public sealed class ChallengeScore
{
    public long ChallengeId { get; set; }
    public long EmployeeId { get; set; }
    public long Score { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
}
