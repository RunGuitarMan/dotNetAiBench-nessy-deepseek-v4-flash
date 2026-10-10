using Microsoft.EntityFrameworkCore;
using Motiva.Domain;

namespace Motiva.Infrastructure;

public sealed class MotivaDbContext(DbContextOptions<MotivaDbContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletBalance> WalletBalances => Set<WalletBalance>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignResource> CampaignResources => Set<CampaignResource>();
    public DbSet<CampaignStream> Streams => Set<CampaignStream>();
    public DbSet<CampaignTask> Tasks => Set<CampaignTask>();
    public DbSet<TaskReward> TaskRewards => Set<TaskReward>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<Achievement> Achievements => Set<Achievement>();
    public DbSet<Challenge> Challenges => Set<Challenge>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<PurchaseSystem> PurchaseSystems => Set<PurchaseSystem>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<IntegrationGrant> IntegrationGrants => Set<IntegrationGrant>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<OperationLine> OperationLines => Set<OperationLine>();
    public DbSet<OperationRequest> OperationRequests => Set<OperationRequest>();
    public DbSet<WalletMovement> WalletMovements => Set<WalletMovement>();
    public DbSet<Progress> Progress => Set<Progress>();
    public DbSet<ProgressEvent> ProgressEvents => Set<ProgressEvent>();
    public DbSet<Completion> Completions => Set<Completion>();
    public DbSet<StreamScore> StreamScores => Set<StreamScore>();
    public DbSet<AchievementGrant> AchievementGrants => Set<AchievementGrant>();
    public DbSet<ChallengeScore> ChallengeScores => Set<ChallengeScore>();
    public DbSet<Statement> Statements => Set<Statement>();
    public DbSet<StatementData> StatementDatas => Set<StatementData>();
    public DbSet<StatementAttempt> StatementAttempts => Set<StatementAttempt>();
    public DbSet<StatementDownload> StatementDownloads => Set<StatementDownload>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Компании
        b.Entity<Company>().HasKey(c => c.Id);

        // Сотрудники: UNIQUE(company_id, master_id)
        b.Entity<Employee>().HasKey(e => e.Id);
        b.Entity<Employee>().HasIndex(e => new { e.CompanyId, e.MasterId }).IsUnique();
        b.Entity<Employee>().Property(e => e.Tags).HasColumnType("jsonb");
        b.Entity<Employee>().HasIndex(e => new { e.CompanyId, e.IsActive });

        // Кошелёк: ровно один на сотрудника
        b.Entity<Wallet>().HasKey(w => w.Id);
        b.Entity<Wallet>().HasIndex(w => w.EmployeeId).IsUnique();

        // Баланс кошелька
        b.Entity<WalletBalance>().HasKey(x => new { x.WalletId, x.ResourceId });
        b.Entity<WalletBalance>().ToTable("wallet_balance", t => t.HasCheckConstraint("CK_wallet_balance_nonneg",
            "\"Balance\" BETWEEN 0 AND 1000000000000000"));

        // Ресурсы: UNIQUE(company_id, lower(code)) — expression index создаётся в миграции (D210)
        b.Entity<Resource>().HasKey(r => r.Id);
        b.Entity<Resource>().HasIndex(r => new { r.CompanyId, r.Code }).IsUnique()
            .HasDatabaseName("uq_resource_company_lower_code");

        // Кампании
        b.Entity<Campaign>().HasKey(c => c.Id);
        b.Entity<Campaign>().Property(c => c.Audience).HasColumnType("jsonb");
        b.Entity<Campaign>().HasIndex(c => new { c.CompanyId, c.Season, c.Code }).IsUnique()
            .HasDatabaseName("uq_campaign_company_season_lower_code");
        b.Entity<Campaign>().HasIndex(c => new { c.CompanyId, c.Season });
        b.Entity<Campaign>().HasIndex(c => c.State);

        // Ресурс кампании
        b.Entity<CampaignResource>().HasKey(x => new { x.CampaignId, x.ResourceId });
        b.Entity<CampaignResource>().ToTable("campaign_resource");

        // Стрим
        b.Entity<CampaignStream>().ToTable("stream").HasKey(s => s.Id);
        b.Entity<CampaignStream>().HasIndex(s => new { s.CampaignId, s.Code }).IsUnique()
            .HasDatabaseName("uq_stream_campaign_lower_code");

        // Задание
        b.Entity<CampaignTask>().ToTable("task", t => t.HasCheckConstraint("CK_task_goal", "\"Goal\" BETWEEN 1 AND 1000000000")).HasKey(t => t.Id);
        b.Entity<CampaignTask>().Property(t => t.Audience).HasColumnType("jsonb");
        b.Entity<CampaignTask>().HasIndex(t => new { t.StreamId, t.Code }).IsUnique()
            .HasDatabaseName("uq_task_stream_lower_code");
        b.Entity<CampaignTask>().HasMany(t => t.Rewards).WithOne().HasForeignKey(r => r.TaskId);

        // Награда задания
        b.Entity<TaskReward>().HasKey(x => new { x.TaskId, x.ResourceId });
        b.Entity<TaskReward>().ToTable("task_reward");

        // Веха
        b.Entity<Milestone>().HasKey(m => m.Id);
        b.Entity<Milestone>().HasIndex(m => new { m.StreamId, m.Threshold }).IsUnique();

        // Достижение: UNIQUE(company_id, lower(code))
        b.Entity<Achievement>().HasKey(a => a.Id);
        b.Entity<Achievement>().HasIndex(a => new { a.CompanyId, a.Code }).IsUnique()
            .HasDatabaseName("uq_achievement_company_lower_code");

        // Челлендж
        b.Entity<Challenge>().HasKey(c => c.Id);

        // Бюджет
        b.Entity<Budget>().HasKey(x => new { x.CampaignId, x.ResourceId });
        b.Entity<Budget>().ToTable("budget", t => t.HasCheckConstraint("CK_budget_nonneg", "\"Remaining\" BETWEEN 0 AND 1000000000000000"));

        // Система покупок
        b.Entity<PurchaseSystem>().HasKey(p => p.Id);
        b.Entity<PurchaseSystem>().Property(p => p.ResourceIds).HasColumnType("jsonb");

        // Интеграция
        b.Entity<Integration>().HasKey(i => i.Id);
        b.Entity<Integration>().HasIndex(i => new { i.CompanyId, i.ServiceSub });

        // Grant — уникальность области через expression index COALESCE создаётся в миграции (D210)
        b.Entity<IntegrationGrant>().HasKey(g => g.Id);
        b.Entity<IntegrationGrant>().HasIndex(g => g.IntegrationId);

        // Операция
        b.Entity<Operation>().HasKey(o => o.Id);
        b.Entity<Operation>().HasIndex(o => new { o.CompanyId, o.InitiatorKey, o.Kind, o.SourceNo }).IsUnique();
        b.Entity<Operation>().HasIndex(o => new { o.EmployeeId, o.OccurredAt });
        b.Entity<Operation>().HasIndex(o => o.CampaignId);
        b.Entity<Operation>().Property(o => o.SourceNo).HasMaxLength(100);
        // частичный уникальный индекс одной успешной отмены (D202)
        b.Entity<Operation>().HasIndex(o => o.OriginalOperationId)
            .IsUnique()
            .HasDatabaseName("uq_cancellation_one_success")
            .HasFilter("\"OriginalOperationId\" IS NOT NULL AND \"Result\" = 0 AND \"Kind\" IN (4, 5)");

        // Позиции операции
        b.Entity<OperationLine>().HasKey(x => new { x.OperationId, x.ResourceId });
        b.Entity<OperationLine>().ToTable("operation_line");

        // Сохранённый запрос
        b.Entity<OperationRequest>().HasKey(r => r.Id);
        b.Entity<OperationRequest>().HasIndex(r => new { r.CompanyId, r.InitiatorKey, r.Kind, r.SourceNo }).IsUnique();
        b.Entity<OperationRequest>().Property(r => r.SourceNo).HasMaxLength(100);

        // Движение кошелька
        b.Entity<WalletMovement>().HasKey(m => m.Id);
        b.Entity<WalletMovement>().HasIndex(m => new { m.WalletId, m.OccurredAt });
        b.Entity<WalletMovement>().HasIndex(m => m.ResourceId);

        // Прогресс
        b.Entity<Progress>().HasKey(p => new { p.EmployeeId, p.TaskId, p.PeriodStart });
        b.Entity<Progress>().ToTable("progress");
        b.Entity<Progress>().HasIndex(p => new { p.EmployeeId, p.PeriodStart });

        // Событие
        b.Entity<ProgressEvent>().HasKey(e => e.Id);
        b.Entity<ProgressEvent>().HasIndex(e => new { e.CompanyId, e.SourceId, e.SourceEventNo }).IsUnique();
        b.Entity<ProgressEvent>().Property(e => e.SourceEventNo).HasMaxLength(100);

        // Завершение
        b.Entity<Completion>().HasKey(c => new { c.EmployeeId, c.TaskId, c.PeriodStart });
        b.Entity<Completion>().ToTable("completion");

        // Очки стрима
        b.Entity<StreamScore>().HasKey(s => new { s.EmployeeId, s.StreamId, s.Season });
        b.Entity<StreamScore>().ToTable("stream_score", t => t.HasCheckConstraint("CK_stream_score_nonneg", "\"Points\" BETWEEN 0 AND 1000000000000000"));

        // Выдача достижения
        b.Entity<AchievementGrant>().HasKey(a => new { a.EmployeeId, a.AchievementId, a.Season });
        b.Entity<AchievementGrant>().ToTable("achievement_grant");

        // Счёт челленджа
        b.Entity<ChallengeScore>().HasKey(c => new { c.ChallengeId, c.EmployeeId });
        b.Entity<ChallengeScore>().ToTable("challenge_score", t => t.HasCheckConstraint("CK_challenge_score_nonneg", "\"Score\" BETWEEN 0 AND 1000000000000000"));

        // Выписка
        b.Entity<Statement>().HasKey(s => s.Id);
        b.Entity<Statement>().HasIndex(s => new { s.OwnerActorType, s.OwnerActorId, s.State });

        // Снимок выписки
        b.Entity<StatementData>().HasKey(d => d.Id);
        b.Entity<StatementData>().HasIndex(d => d.StatementId).IsUnique();

        // Попытка
        b.Entity<StatementAttempt>().HasKey(a => a.StatementId);

        // Ссылка
        b.Entity<StatementDownload>().HasKey(d => d.Id);
        b.Entity<StatementDownload>().HasIndex(d => d.StatementId);

        // Аудит
        b.Entity<AuditEntry>().HasKey(a => a.Id);
        b.Entity<AuditEntry>().Property(a => a.Details).HasColumnType("jsonb");
        b.Entity<AuditEntry>().HasIndex(a => new { a.Target, a.OccurredAt });
    }
}
