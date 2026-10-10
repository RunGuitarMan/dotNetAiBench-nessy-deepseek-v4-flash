namespace Motiva.Api;

public sealed record PageResponse<T>(List<T> Items, string? NextCursor);

// Аутентификация bootstrap (доверенный вход, T03)
public sealed record BootstrapRequest(string CompanyName, int AdminMasterId);
public sealed record BootstrapResponse(Guid CompanyId, long AdminEmployeeId);

// Сотрудники
public sealed record EmployeeDto(long Id, int MasterId, bool IsActive, List<string>? Tags, string? Etag);
public sealed record EmployeeCreateDto(int MasterId, List<string>? Tags);
public sealed record EmployeePatchDto(List<string>? Tags, bool? IsActive);

// Ресурсы
public sealed record ResourceDto(long Id, string Code, string Name, string Status, string? Etag);
public sealed record ResourceCreateDto(string Code, string Name);
public sealed record ResourcePatchDto(string? Name, string? Status);

// Интеграции / grants
public sealed record IntegrationDto(long Id, string Name, string ServiceSub, Guid CompanyId);
public sealed record IntegrationCreateDto(string Name, string ServiceSub);
public sealed record GrantDto(string Kind, long? CampaignId, long? ResourceId, long? PurchaseSystemId);
public sealed record GrantListDto(List<GrantDto> Grants);

// Кампании
public sealed record AudienceDto(List<string>? Any, List<string>? All, List<string>? None);
public sealed record CampaignResourceDto(long ResourceId, bool? AllocationRequested);
public sealed record CampaignDto(long Id, string Code, string Name, string? Description, int Season,
    DateTimeOffset Start, DateTimeOffset End, string State, long? OwnerEmployeeId, AudienceDto? Audience,
    List<CampaignResourceDto>? Resources, string? Etag);
public sealed record CampaignCreateDto(string Code, string Name, string? Description, int Season,
    DateTimeOffset Start, DateTimeOffset End, AudienceDto? Audience, List<CampaignResourceDto>? Resources);
public sealed record CampaignPatchDto(string? Code, string? Name, string? Description, int? Season,
    DateTimeOffset? Start, DateTimeOffset? End, AudienceDto? Audience, List<CampaignResourceDto>? Resources,
    long? OwnerEmployeeId, string? Status);

// Стримы / задания / вехи
public sealed record StreamDto(long Id, long CampaignId, string Code, string Name, AudienceDto? Audience, string Status, string? Etag);
public sealed record StreamCreateDto(string Code, string Name);
public sealed record StreamPatchDto(string? Name, string? Status);
public sealed record RewardLineDto(long ResourceId, int Amount);
public sealed record TaskDto(long Id, long StreamId, string Code, string Name, string? Description, int Goal,
    string Periodicity, int StreamPoints, AudienceDto? Audience, List<RewardLineDto>? Rewards, string Status, string? Etag);
public sealed record TaskCreateDto(string Code, string Name, string? Description, int Goal, string Periodicity,
    int StreamPoints, AudienceDto? Audience, List<RewardLineDto>? Rewards);
public sealed record TaskPatchDto(string? Name, string? Description, int? Goal, string? Periodicity,
    int? StreamPoints, AudienceDto? Audience, List<RewardLineDto>? Rewards, string? Status);
public sealed record MilestoneDto(long Id, long StreamId, int Threshold, long AchievementId, string? Etag);
public sealed record MilestoneCreateDto(int Threshold, long AchievementId);
public sealed record MilestonePatchDto(int? Threshold, long? AchievementId);

// Челленджи / рейтинг
public sealed record ChallengeDto(long Id, long StreamId, DateTimeOffset Start, DateTimeOffset End, string State, string? Etag);
public sealed record ChallengeCreateDto(long StreamId, DateTimeOffset Start, DateTimeOffset End);
public sealed record RatingRowDto(int MasterId, long Score, int Place);
public sealed record LeaderboardDto(int Limit, List<RatingRowDto> Top, DateTimeOffset AsOf);
public sealed record OwnRatingDto(bool Present, int? MasterId, long? Score, int? Place);

// Достижения
public sealed record AchievementDto(long Id, string Code, string Name, string? Description, string? Etag);
public sealed record AchievementCreateDto(string Code, string Name, string? Description);
public sealed record AchievementPatchDto(string? Name, string? Description);
public sealed record AchievementGrantDto(long EmployeeId, long AchievementId, int Season, DateTimeOffset GrantedAt);

// Бюджет
public sealed record BudgetDto(long CampaignId, long ResourceId, long Remaining);
public sealed record BudgetAllocationDto(long ResourceId, int Amount);

// Системы покупок
public sealed record PurchaseSystemDto(long Id, string Name, List<long>? Resources, string? Etag);
public sealed record PurchaseSystemCreateDto(string Name);
public sealed record PurchaseSystemPatchDto(string? Name);

// Каталог / прогресс / кошелёк
public sealed record CatalogItemDto(long CampaignId, string Code, string Name, DateTimeOffset AsOf, List<CatalogTaskDto>? Tasks);
public sealed record CatalogTaskDto(long TaskId, string Code, string Name, int Goal);
public sealed record ProgressDto(long TaskId, string Period, int Value, int Goal, bool Completed);
public sealed record StreamScoreDto(long StreamId, int Season, long Points);
public sealed record RewardDto(long TaskId, string Result, List<RewardLineDto>? Lines, string? RejectReason);
public sealed record BalanceDto(long ResourceId, string Code, long Balance);
public sealed record WalletDto(long EmployeeId, List<BalanceDto> Balances);
public sealed record MovementDto(long Id, long OperationId, long ResourceId, long Delta, long BalanceAfter,
    DateTimeOffset OccurredAt, string Kind, long? CampaignId, long? OriginalOperationId);
public sealed record OperationLineDto(long ResourceId, int Amount);
public sealed record OperationDto(long Id, string Kind, string InitiatorKey, string? SourceNo, string? Basis,
    long? EmployeeId, string Result, string? RejectReason, DateTimeOffset OccurredAt, long? CampaignId,
    long? PurchaseSystemId, long? OriginalOperationId, List<OperationLineDto>? Lines);
public sealed record OutcomeDto(long OperationId, string Status, string? RejectReason);
public sealed record EventResultDto(bool Accepted, int CreditedIncrease, bool? Completed, EventRewardDto? Reward);
public sealed record EventRewardDto(string? Result, string? Reason);

// Операции
public sealed record SpendRequestDto(long ResourceId, int Amount, long PurchaseSystemId, string Basis);
public sealed record InternalSpendRequestDto(long EmployeeId, long ResourceId, int Amount, long PurchaseSystemId, string Basis);
public sealed record ManualAccrualRequestDto(long CampaignId, long EmployeeId, long ResourceId, int Amount, string Basis);
public sealed record InternalManualAccrualRequestDto(long CampaignId, long EmployeeId, long ResourceId, int Amount, string Basis);
public sealed record CancellationRequestDto(long OriginalOperationId, string Basis);
public sealed record InternalRefundRequestDto(string Basis);
public sealed record EventRequestDto(long EmployeeId, long TaskId, int Increase, string SourceEventNo);

// Выписки
public sealed record StatementDto(long Id, string State, string? Scope, DateTimeOffset? From, DateTimeOffset? To,
    long? ResourceId, string? Etag);
public sealed record StatementRequestDto(long? EmployeeId, DateTimeOffset From, DateTimeOffset To, long? ResourceId);
public sealed record DownloadLinkDto(string Url, DateTimeOffset ExpiresAt);

// Аудит
public sealed record AuditEntryDto(long Id, string Actor, string Target, string Action, DateTimeOffset OccurredAt, Dictionary<string, string>? Details);
