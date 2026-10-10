using System.Text.Json;
using Motiva.Application.Ports;
using Motiva.Domain;

namespace Motiva.Application.UseCases;

public sealed class MeReadUseCases(IUnitOfWork db, ICurrentActor current, ICompanyCalendar calendar, ITime time, ICache cache)
{
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromSeconds(30); // обязательное T06

    private async Task<Employee> SelfAsync(Actor actor, CancellationToken ct)
    {
        if (actor.Type != ActorType.User || actor.MasterId is null)
        {
            throw MotivaException.Forbidden("Только сотрудник");
        }

        return await db.Set<Employee>().FirstOrDefaultAsync(e => e.CompanyId == actor.CompanyId && e.MasterId == actor.MasterId, ct)
            ?? throw MotivaException.NotFound("Профиль не найден");
    }

    /// <summary>
    /// Каталог доступных опубликованных кампаний текущего сезона (B11/B34).
    /// T06/D209: неперсональные структурные данные кешируются (свежесть ≤ 30 с, asOf);
    /// фильтрация по актуальной аудитории сотрудника выполняется на чтении.
    /// </summary>
    public async Task<CatalogView> GetMyCatalogAsync(CancellationToken ct)
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        var employee = await SelfAsync(actor, ct);
        var season = calendar.SeasonOf(time.UtcNow);
        var asOf = time.UtcNow;
        var key = $"catalog:{actor.CompanyId}:{season}";

        var structural = await TryReadCatalogAsync(key, asOf, ct)
            ?? await LoadCatalogAsync(actor.CompanyId, season, asOf, key, ct);

        var result = new List<CatalogItemView>();
        foreach (var campaign in structural.Campaigns)
        {
            if (!AudienceRules.IsIncluded(campaign.Audience, employee.Tags))
            {
                continue;
            }

            var tasks = new List<CatalogTaskView>();
            foreach (var task in campaign.Tasks)
            {
                if (AudienceRules.IsIncluded(task.Audience, employee.Tags))
                {
                    tasks.Add(new CatalogTaskView(task.TaskId, task.Code, task.Name, task.Goal));
                }
            }
            if (tasks.Count > 0)
            {
                result.Add(new CatalogItemView(campaign.CampaignId, campaign.Code, campaign.Name, asOf, tasks));
            }
        }

        return new CatalogView(result);
    }

    private async Task<CachedCatalog?> TryReadCatalogAsync(string key, DateTimeOffset now, CancellationToken ct)
    {
        var raw = await cache.ReadAsync(key, ct);
        if (raw is null)
        {
            return null;
        }

        try
        {
            var cached = JsonSerializer.Deserialize<CachedCatalog>(raw);
            if (cached is null || now - cached.AsOf > CatalogTtl)
            {
                return null; // просроченный/повреждённый снимок → miss (T06)
            }

            return cached;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<CachedCatalog> LoadCatalogAsync(Guid companyId, int season, DateTimeOffset asOf, string key, CancellationToken ct)
    {
        var campaigns = await db.Set<Campaign>()
            .Where(c => c.CompanyId == companyId && c.State == CampaignState.Published && c.Season == season)
            .ToListAsync(ct);
        var items = new List<CachedCampaign>();
        foreach (var campaign in campaigns)
        {
            var streams = await db.Set<CampaignStream>().Where(s => s.CampaignId == campaign.Id && s.Status != ResourceStatus.Archived).ToListAsync(ct);
            var tasks = new List<CachedTask>();
            foreach (var stream in streams)
            {
                var streamTasks = await db.Set<CampaignTask>().Where(t => t.StreamId == stream.Id && t.Status != ResourceStatus.Archived).ToListAsync(ct);
                foreach (var task in streamTasks)
                {
                    tasks.Add(new CachedTask(task.Id, task.Code, task.Name, task.Goal, task.Audience));
                }
            }
            items.Add(new CachedCampaign(campaign.Id, campaign.Code, campaign.Name, campaign.Audience, tasks));
        }

        var catalog = new CachedCatalog(asOf, items);
        try
        {
            // write-if-asOf-newer; просроченный снимок не перезаписывает свежий (I202)
            await cache.WriteIfNewerAsync(key, JsonSerializer.Serialize(catalog), asOf, CatalogTtl, ct);
        }
        catch
        {
            // отказ кеша не блокирует выдачу каталога (T06: PostgreSQL fallback)
        }

        return catalog;
    }

    public async Task<List<ProgressView>> GetMyProgressAsync(long? taskId, Periodicity? period, CancellationToken ct)
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        var employee = await SelfAsync(actor, ct);
        var q = db.Set<Progress>().Where(p => p.EmployeeId == employee.Id);
        if (taskId is long tid)
        {
            q = q.Where(p => p.TaskId == tid);
        }

        var rows = await q.ToListAsync(ct);
        var taskIds = rows.Select(r => r.TaskId).Distinct().ToList();
        var tasks = await db.Set<CampaignTask>().Where(t => taskIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var result = new List<ProgressView>();
        foreach (var row in rows)
        {
            var task = tasks.GetValueOrDefault(row.TaskId);
            if (task is null)
            {
                continue;
            }

            result.Add(new ProgressView(task.Id, task.Periodicity.ToString().ToLower(), row.Value, task.Goal,
                await db.Set<Completion>().AnyAsync(c => c.EmployeeId == employee.Id && c.TaskId == task.Id && c.PeriodStart == row.PeriodStart, ct)));
        }
        return result.OrderBy(p => p.TaskId).ToList();
    }

    public async Task<long> GetMyStreamScoreAsync(long streamId, CancellationToken ct)
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        var employee = await SelfAsync(actor, ct);
        var season = calendar.SeasonOf(time.UtcNow);
        var score = await db.Set<StreamScore>().FirstOrDefaultAsync(s => s.EmployeeId == employee.Id && s.StreamId == streamId && s.Season == season, ct);
        return score?.Points ?? 0;
    }

    public async Task<RewardView> GetMyRewardAsync(long taskId, Periodicity? period, CancellationToken ct)
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        var employee = await SelfAsync(actor, ct);
        var task = await db.Set<CampaignTask>().FirstOrDefaultAsync(t => t.Id == taskId, ct) ?? throw MotivaException.NotFound("Задание не найдено");
        var periodStart = calendar.PeriodStart(time.UtcNow, task.Periodicity);
        var completion = await db.Set<Completion>().FirstOrDefaultAsync(c => c.EmployeeId == employee.Id && c.TaskId == taskId && c.PeriodStart == periodStart, ct);
        if (completion is null)
        {
            return new RewardView(taskId, "none", new(), null);
        }

        var ev = await db.Set<ProgressEvent>().Where(e => e.EmployeeId == employee.Id && e.TaskId == taskId && e.Completed)
            .OrderByDescending(e => e.AcceptedAt).FirstOrDefaultAsync(ct);
        if (ev is null)
        {
            return new RewardView(taskId, "none", new(), null);
        }

        if (ev.RewardResult == "none" || task.Rewards.Count == 0)
        {
            return new RewardView(taskId, "none", task.Rewards.Select(r => new RewardLineView(r.ResourceId, r.Amount)).ToList(), null);
        }

        var lines = task.Rewards.Select(r => new RewardLineView(r.ResourceId, r.Amount)).ToList();
        return new RewardView(taskId, ev.RewardResult ?? "none", lines, ev.RewardReason);
    }

    public async Task<List<AchievementGrant>> GetMyAchievementGrantsAsync(CancellationToken ct)
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        var employee = await SelfAsync(actor, ct);
        return await db.Set<AchievementGrant>().Where(a => a.EmployeeId == employee.Id).OrderByDescending(a => a.Season).ToListAsync(ct);
    }
}

public sealed record CatalogView(List<CatalogItemView> Items);
public sealed record CatalogItemView(long CampaignId, string Code, string Name, DateTimeOffset AsOf, List<CatalogTaskView> Tasks);
public sealed record CatalogTaskView(long TaskId, string Code, string Name, int Goal);
public sealed record ProgressView(long TaskId, string Period, int Value, int Goal, bool Completed);
public sealed record RewardView(long TaskId, string Result, List<RewardLineView> Lines, string? RejectReason);
public sealed record RewardLineView(long ResourceId, int Amount);

/// <summary>Неперсональный структурный кеш каталога (T06/D209).</summary>
internal sealed record CachedCatalog(DateTimeOffset AsOf, List<CachedCampaign> Campaigns);
internal sealed record CachedCampaign(long CampaignId, string Code, string Name, Audience Audience, List<CachedTask> Tasks);
internal sealed record CachedTask(long TaskId, string Code, string Name, int Goal, Audience Audience);
