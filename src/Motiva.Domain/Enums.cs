namespace Motiva.Domain;

/// <summary>Периодичность задания (B13).</summary>
public enum Periodicity
{
    Day,
    Month,
    Quarter,
    HalfYear,
    Year,
}

/// <summary>Состояние кампании (B09): Черновик → Опубликована → Архив.</summary>
public enum CampaignState
{
    Draft,
    Published,
    Archived,
}

/// <summary>Архивное состояние настраиваемого объекта.</summary>
public enum ResourceStatus
{
    Active,
    Archived,
}

/// <summary>Вид операции (D201 / B28).</summary>
public enum OperationKind
{
    BudgetAllocation,
    ManualAccrual,
    TaskReward,
    Spend,
    AccrualCancellation,
    SpendRefund,
}

/// <summary>Результат экономической операции (T04).</summary>
public enum OperationResult
{
    Posted,
    Declined,
}

/// <summary>Вид grant интеграции (B04 / D210).</summary>
public enum GrantKind
{
    Progress,
    Accrual,
    Spend,
}

/// <summary>Состояние выписки (B36).</summary>
public enum StatementState
{
    Pending,
    Forming,
    Ready,
    Error,
    Deleted,
}

/// <summary>Область выписки (B35).</summary>
public enum StatementScope
{
    Employee,
    Company,
}

/// <summary>Состояние челленджа (B33).</summary>
public enum ChallengeState
{
    Open,
    Final,
}

/// <summary>Вид субъекта инициатора (D201).</summary>
public enum ActorType
{
    User,
    Service,
}
