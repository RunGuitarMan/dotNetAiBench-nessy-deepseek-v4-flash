namespace Motiva.Domain;

/// <summary>Аудитория — три набора тегов (B11). Пустой набор не ограничивает.</summary>
public sealed class Audience
{
    public List<string> Any { get; set; } = new();
    public List<string> All { get; set; } = new();
    public List<string> None { get; set; } = new();
}

/// <summary>Кампания (B08/B09).</summary>
public sealed class Campaign
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Season { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public CampaignState State { get; set; } = CampaignState.Draft;
    public long? OwnerEmployeeId { get; set; }
    public Audience Audience { get; set; } = new();
    public List<CampaignResource> Resources { get; set; } = new();
}

/// <summary>Ресурс в наборе кампании (B08e).</summary>
public sealed class CampaignResource
{
    public long CampaignId { get; set; }
    public long ResourceId { get; set; }
    /// <summary>Запрос на выделение бюджета (подтверждается admin отдельной операцией B18).</summary>
    public bool AllocationRequested { get; set; }
}

/// <summary>Стрим (B08d). Аудитория наследуется от кампании.</summary>
public sealed class CampaignStream
{
    public long Id { get; set; }
    public long CampaignId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ResourceStatus Status { get; set; } = ResourceStatus.Active;
}

/// <summary>Задание (B12).</summary>
public sealed class CampaignTask
{
    public long Id { get; set; }
    public long StreamId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Goal { get; set; }
    public Periodicity Periodicity { get; set; }
    public int StreamPoints { get; set; }
    public Audience Audience { get; set; } = new();
    public ResourceStatus Status { get; set; } = ResourceStatus.Active;
    public List<TaskReward> Rewards { get; set; } = new();
}

/// <summary>Позиция награды задания (B12c/B20).</summary>
public sealed class TaskReward
{
    public long TaskId { get; set; }
    public long ResourceId { get; set; }
    public int Amount { get; set; }
}

/// <summary>Веха стрима (B29).</summary>
public sealed class Milestone
{
    public long Id { get; set; }
    public long StreamId { get; set; }
    public int Threshold { get; set; }
    public long AchievementId { get; set; }
}

/// <summary>Определение достижения (B30).</summary>
public sealed class Achievement
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

/// <summary>Челлендж по стриму (B31). Итог неизменяем после final (B33).</summary>
public sealed class Challenge
{
    public long Id { get; set; }
    public long StreamId { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public ChallengeState State { get; set; } = ChallengeState.Open;
}

/// <summary>Бюджет на пару (кампания, ресурс) (B19).</summary>
public sealed class Budget
{
    public long CampaignId { get; set; }
    public long ResourceId { get; set; }
    public long Remaining { get; set; }
}

/// <summary>Система покупок (B23d).</summary>
public sealed class PurchaseSystem
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<long> ResourceIds { get; set; } = new();
}

/// <summary>Интеграция (B04/T03).</summary>
public sealed class Integration
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Проверенный sub сервиса (actorType=service).</summary>
    public string ServiceSub { get; set; } = string.Empty;
}

/// <summary>Разрешение интеграции (B04/D210).</summary>
public sealed class IntegrationGrant
{
    public long Id { get; set; }
    public long IntegrationId { get; set; }
    public GrantKind Kind { get; set; }
    public long? CampaignId { get; set; }
    public long? ResourceId { get; set; }
    public long? PurchaseSystemId { get; set; }
}
