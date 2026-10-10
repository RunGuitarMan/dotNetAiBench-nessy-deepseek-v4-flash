using Motiva.Application.Ports;
using Motiva.Domain;

namespace Motiva.Application.UseCases;

public sealed class StreamTaskUseCases(IUnitOfWork db, ICurrentActor current)
{
    private (Actor actor, bool isAdmin) Current()
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        return (actor, actor.IsAdmin);
    }

    private async Task<Campaign> GetCampaignAsync(Guid companyId, long campaignId, CancellationToken ct)
    {
        return await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == campaignId && c.CompanyId == companyId, ct)
            ?? throw MotivaException.NotFound("Кампания не найдена");
    }

    private async Task EnsureConfigurableAsync(Guid companyId, long campaignId, int? masterId, CancellationToken ct)
    {
        var campaign = await GetCampaignAsync(companyId, campaignId, ct);
        if (campaign.State != CampaignState.Draft)
        {
            throw MotivaException.Conflict("Настройка после публикации запрещена (B09)");
        }

        if (current.Actor!.IsAdmin)
        {
            return;
        }

        var owner = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == campaign.OwnerEmployeeId && e.CompanyId == companyId, ct);
        if (owner is null || owner.MasterId != masterId)
        {
            throw MotivaException.Forbidden("Требуется владелец или Admin");
        }
    }

    private async Task<CampaignStream> GetStreamAsync(Guid companyId, long streamId, CancellationToken ct)
    {
        var stream = await db.Set<CampaignStream>().FirstOrDefaultAsync(s => s.Id == streamId, ct)
            ?? throw MotivaException.NotFound("Стрим не найден");
        var campaign = await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == stream.CampaignId && c.CompanyId == companyId, ct)
            ?? throw MotivaException.NotFound("Стрим не найден");
        return stream;
    }

    public async Task<CampaignStream> CreateStreamAsync(long campaignId, string code, string name, CancellationToken ct)
    {
        var (actor, _) = Current();
        await EnsureConfigurableAsync(actor.CompanyId, campaignId, actor.MasterId, ct);
        if (!DomainRules.IsValidCode(code))
        {
            throw MotivaException.Validation("Некорректный код");
        }

        if (!DomainRules.IsValidName(name))
        {
            throw MotivaException.Validation("Некорректное название");
        }

        if (await db.Set<CampaignStream>().AnyAsync(s => s.CampaignId == campaignId && s.Code.ToLower() == code.ToLower(), ct))
        {
            throw MotivaException.Conflict("Код стрима уже используется");
        }

        var stream = new CampaignStream { CampaignId = campaignId, Code = code, Name = name };
        db.Add(stream);
        await db.SaveChangesAsync(ct);
        return stream;
    }

    public async Task<CampaignStream> UpdateStreamAsync(long streamId, string? name, ResourceStatus? status, string? expectedEtag, CancellationToken ct)
    {
        var (actor, _) = Current();
        var stream = await GetStreamAsync(actor.CompanyId, streamId, ct);
        var campaign = await GetCampaignAsync(actor.CompanyId, stream.CampaignId, ct);
        if (expectedEtag is null)
        {
            throw MotivaException.PreconditionRequired("Требуется If-Match");
        }

        if (stream.Etag() != expectedEtag)
        {
            throw MotivaException.PreconditionFailed("ETag устарел");
        }

        if (name is not null)
        {
            if (!DomainRules.IsValidName(name))
            {
                throw MotivaException.Validation("Некорректное название");
            }

            stream.Name = name;
        }
        if (status == ResourceStatus.Archived)
        {
            if (campaign.State != CampaignState.Published && !current.Actor!.IsAdmin)
            {
                throw MotivaException.Forbidden("Архив стрима черновика — только Admin");
            }

            stream.Status = ResourceStatus.Archived;
        }
        db.Update(stream);
        await db.SaveChangesAsync(ct);
        return stream;
    }

    public async Task<List<CampaignStream>> ListStreamsAsync(long campaignId, CancellationToken ct)
    {
        var (actor, _) = Current();
        await GetCampaignAsync(actor.CompanyId, campaignId, ct);
        return await db.Set<CampaignStream>().Where(s => s.CampaignId == campaignId).OrderBy(s => s.Id).ToListAsync(ct);
    }

    public async Task<CampaignTask> CreateTaskAsync(long streamId, string code, string name, string? description,
        int goal, Periodicity periodicity, int streamPoints, Audience audience,
        IReadOnlyList<(long resourceId, int amount)> rewards, CancellationToken ct)
    {
        var (actor, _) = Current();
        var stream = await GetStreamAsync(actor.CompanyId, streamId, ct);
        await EnsureConfigurableAsync(actor.CompanyId, stream.CampaignId, actor.MasterId, ct);
        ValidateTask(code, name, description, goal, periodicity, streamPoints, audience);
        if (await db.Set<CampaignTask>().AnyAsync(t => t.StreamId == streamId && t.Code.ToLower() == code.ToLower(), ct))
        {
            throw MotivaException.Conflict("Код задания уже используется");
        }

        var campaignResources = await db.Set<CampaignResource>().Where(cr => cr.CampaignId == stream.CampaignId).Select(cr => cr.ResourceId).ToListAsync(ct);
        ValidateRewards(rewards, campaignResources);

        var task = new CampaignTask
        {
            StreamId = streamId,
            Code = code,
            Name = name,
            Description = description,
            Goal = goal,
            Periodicity = periodicity,
            StreamPoints = streamPoints,
            Audience = audience,
        };
        db.Add(task);
        await db.SaveChangesAsync(ct);
        foreach (var (resourceId, amount) in rewards)
        {
            db.Add(new TaskReward { TaskId = task.Id, ResourceId = resourceId, Amount = amount });
        }

        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<CampaignTask> UpdateTaskAsync(long taskId, TaskPatchData patch, string? expectedEtag, CancellationToken ct)
    {
        var (actor, _) = Current();
        var task = await db.Set<CampaignTask>().FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw MotivaException.NotFound("Задание не найдено");
        var stream = await GetStreamAsync(actor.CompanyId, task.StreamId, ct);
        var campaign = await GetCampaignAsync(actor.CompanyId, stream.CampaignId, ct);
        if (expectedEtag is null)
        {
            throw MotivaException.PreconditionRequired("Требуется If-Match");
        }

        if (task.Etag() != expectedEtag)
        {
            throw MotivaException.PreconditionFailed("ETag устарел");
        }

        if (campaign.State != CampaignState.Draft)
        {
            if (patch.Goal is not null || patch.Periodicity is not null || patch.StreamPoints is not null || patch.Rewards is not null)
            {
                throw MotivaException.Conflict("После публикации цель/периодичность/очки/награды менять нельзя (B09e)");
            }
        }
        if (patch.Name is not null)
        {
            if (!DomainRules.IsValidName(patch.Name))
            {
                throw MotivaException.Validation("Некорректное название");
            }

            task.Name = patch.Name;
        }
        if (patch.Description is not null)
        {
            task.Description = patch.Description;
        }

        if (patch.Audience is not null)
        {
            CampaignUseCases.ValidateAudience(patch.Audience);
            task.Audience = patch.Audience;
        }
        if (patch.Status == ResourceStatus.Archived)
        {
            if (campaign.State != CampaignState.Published && !current.Actor!.IsAdmin)
            {
                throw MotivaException.Forbidden("Архив задания черновика — только Admin");
            }

            task.Status = ResourceStatus.Archived;
        }
        if (campaign.State == CampaignState.Draft)
        {
            if (patch.Goal is int g)
            {
                if (!DomainRules.IsValidPositive(g))
                {
                    throw MotivaException.Validation("Цель вне диапазона");
                }

                task.Goal = g;
            }
            if (patch.Periodicity is Periodicity p)
            {
                task.Periodicity = p;
            }

            if (patch.StreamPoints is int sp)
            {
                if (sp is < 0 or > (int)DomainRules.MaxSingleValue)
                {
                    throw MotivaException.Validation("Очки вне диапазона");
                }

                task.StreamPoints = sp;
            }
            if (patch.Rewards is not null)
            {
                var campaignResources = await db.Set<CampaignResource>().Where(cr => cr.CampaignId == stream.CampaignId).Select(cr => cr.ResourceId).ToListAsync(ct);
                ValidateRewards(patch.Rewards, campaignResources);
                foreach (var r in await db.Set<TaskReward>().Where(r => r.TaskId == taskId).ToListAsync(ct))
                {
                    db.Remove(r);
                }

                foreach (var (resourceId, amount) in patch.Rewards)
                {
                    db.Add(new TaskReward { TaskId = taskId, ResourceId = resourceId, Amount = amount });
                }
            }
        }
        db.Update(task);
        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<CampaignTask> GetTaskAsync(long taskId, CancellationToken ct)
    {
        var (actor, _) = Current();
        var task = await db.Set<CampaignTask>().FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw MotivaException.NotFound("Задание не найдено");
        await GetStreamAsync(actor.CompanyId, task.StreamId, ct);
        return task;
    }

    public async Task<List<CampaignTask>> ListTasksAsync(long streamId, CancellationToken ct)
    {
        var (actor, _) = Current();
        await GetStreamAsync(actor.CompanyId, streamId, ct);
        return await db.Set<CampaignTask>().Where(t => t.StreamId == streamId).OrderBy(t => t.Id).ToListAsync(ct);
    }

    private static void ValidateTask(string code, string name, string? description, int goal, Periodicity periodicity, int streamPoints, Audience audience)
    {
        if (!DomainRules.IsValidCode(code))
        {
            throw MotivaException.Validation("Некорректный код");
        }

        if (!DomainRules.IsValidName(name))
        {
            throw MotivaException.Validation("Некорректное название");
        }

        if (!DomainRules.IsValidDescription(description))
        {
            throw MotivaException.Validation("Некорректное описание");
        }

        if (!DomainRules.IsValidPositive(goal))
        {
            throw MotivaException.Validation("Цель вне диапазона");
        }

        if (streamPoints is < 0 or > (int)DomainRules.MaxSingleValue)
        {
            throw MotivaException.Validation("Очки вне диапазона");
        }

        CampaignUseCases.ValidateAudience(audience);
    }

    private static void ValidateRewards(IReadOnlyList<(long resourceId, int amount)> rewards, IReadOnlyCollection<long> campaignResources)
    {
        if (rewards.Select(r => r.resourceId).Distinct().Count() != rewards.Count)
        {
            throw MotivaException.Validation("Ресурс в награде не должен повторяться (B12c)");
        }

        foreach (var (resourceId, amount) in rewards)
        {
            if (!DomainRules.IsValidPositive(amount))
            {
                throw MotivaException.Validation("Сумма награды вне диапазона");
            }

            if (!campaignResources.Contains(resourceId))
            {
                throw MotivaException.Validation("Ресурс награды не входит в кампанию (B12c)");
            }
        }
    }
}

public sealed record TaskPatchData(
    string? Name, string? Description, int? Goal, Periodicity? Periodicity,
    int? StreamPoints, Audience? Audience, IReadOnlyList<(long resourceId, int amount)>? Rewards, ResourceStatus? Status);

public sealed class MilestoneChallengeUseCases(IUnitOfWork db, ICurrentActor current)
{
    private (Actor actor, bool isAdmin) Current()
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        return (actor, actor.IsAdmin);
    }

    private async Task<CampaignStream> GetStreamForConfigAsync(Guid companyId, long streamId, int? masterId, CancellationToken ct)
    {
        var stream = await db.Set<CampaignStream>().FirstOrDefaultAsync(s => s.Id == streamId, ct)
            ?? throw MotivaException.NotFound("Стрим не найден");
        var campaign = await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == stream.CampaignId && c.CompanyId == companyId, ct)
            ?? throw MotivaException.NotFound("Стрим не найден");
        if (campaign.State != CampaignState.Draft)
        {
            throw MotivaException.Conflict("Настройка после публикации запрещена");
        }

        if (current.Actor!.IsAdmin)
        {
            return stream;
        }

        var owner = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == campaign.OwnerEmployeeId && e.CompanyId == companyId, ct);
        if (owner is null || owner.MasterId != masterId)
        {
            throw MotivaException.Forbidden("Требуется владелец или Admin");
        }

        return stream;
    }

    public async Task<Milestone> CreateMilestoneAsync(long streamId, int threshold, long achievementId, CancellationToken ct)
    {
        var (actor, _) = Current();
        await GetStreamForConfigAsync(actor.CompanyId, streamId, actor.MasterId, ct);
        if (!DomainRules.IsValidPositive(threshold))
        {
            throw MotivaException.Validation("Порог вне диапазона");
        }

        if (!await db.Set<Achievement>().AnyAsync(a => a.Id == achievementId && a.CompanyId == actor.CompanyId, ct))
        {
            throw MotivaException.Validation("Достижение не найдено");
        }

        if (await db.Set<Milestone>().AnyAsync(m => m.StreamId == streamId && m.Threshold == threshold, ct))
        {
            throw MotivaException.Conflict("Веха с таким порогом уже есть");
        }

        var milestone = new Milestone { StreamId = streamId, Threshold = threshold, AchievementId = achievementId };
        db.Add(milestone);
        await db.SaveChangesAsync(ct);
        return milestone;
    }

    public async Task<Milestone> UpdateMilestoneAsync(long id, int? threshold, long? achievementId, string? expectedEtag, CancellationToken ct)
    {
        var (actor, _) = Current();
        var milestone = await db.Set<Milestone>().FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw MotivaException.NotFound("Веха не найдена");
        await GetStreamForConfigAsync(actor.CompanyId, milestone.StreamId, actor.MasterId, ct);
        if (expectedEtag is null)
        {
            throw MotivaException.PreconditionRequired("Требуется If-Match");
        }

        if (milestone.Etag() != expectedEtag)
        {
            throw MotivaException.PreconditionFailed("ETag устарел");
        }

        if (threshold is int t)
        {
            if (!DomainRules.IsValidPositive(t))
            {
                throw MotivaException.Validation("Порог вне диапазона");
            }

            milestone.Threshold = t;
        }
        if (achievementId is long a)
        {
            if (!await db.Set<Achievement>().AnyAsync(x => x.Id == a && x.CompanyId == actor.CompanyId, ct))
            {
                throw MotivaException.Validation("Достижение не найдено");
            }

            milestone.AchievementId = a;
        }
        db.Update(milestone);
        await db.SaveChangesAsync(ct);
        return milestone;
    }

    public async Task DeleteMilestoneAsync(long id, string? expectedEtag, CancellationToken ct)
    {
        var (actor, _) = Current();
        var milestone = await db.Set<Milestone>().FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw MotivaException.NotFound("Веха не найдена");
        await GetStreamForConfigAsync(actor.CompanyId, milestone.StreamId, actor.MasterId, ct);
        if (expectedEtag is null)
        {
            throw MotivaException.PreconditionRequired("Требуется If-Match");
        }

        if (milestone.Etag() != expectedEtag)
        {
            throw MotivaException.PreconditionFailed("ETag устарел");
        }

        db.Remove(milestone);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<Milestone>> ListMilestonesAsync(long streamId, CancellationToken ct)
    {
        var (actor, _) = Current();
        await GetStreamForConfigAsync(actor.CompanyId, streamId, actor.MasterId, ct);
        return await db.Set<Milestone>().Where(m => m.StreamId == streamId).OrderBy(m => m.Threshold).ToListAsync(ct);
    }

    public async Task<Challenge> CreateChallengeAsync(long streamId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var (actor, _) = Current();
        var stream = await GetStreamForConfigAsync(actor.CompanyId, streamId, actor.MasterId, ct);
        if (start >= end)
        {
            throw MotivaException.Validation("start раньше end");
        }

        var campaign = await db.Set<Campaign>().FirstAsync(c => c.Id == stream.CampaignId, ct);
        if (start < campaign.Start || end > campaign.End)
        {
            throw MotivaException.Validation("Челлендж внутри сроков кампании (B31)");
        }

        var challenge = new Challenge { StreamId = streamId, Start = start, End = end };
        db.Add(challenge);
        await db.SaveChangesAsync(ct);
        return challenge;
    }

    public async Task<Challenge> GetChallengeAsync(long id, CancellationToken ct)
    {
        var (actor, _) = Current();
        var challenge = await db.Set<Challenge>().FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw MotivaException.NotFound("Челлендж не найден");
        await GetStreamForConfigAsync(actor.CompanyId, challenge.StreamId, actor.MasterId, ct);
        return challenge;
    }

    public async Task<List<Challenge>> ListChallengesAsync(CancellationToken ct)
    {
        var (actor, _) = Current();
        return await db.Set<Challenge>()
            .Join(db.Set<CampaignStream>(), c => c.StreamId, s => s.Id, (c, s) => new { c, s })
            .Join(db.Set<Campaign>(), x => x.s.CampaignId, ca => ca.Id, (x, ca) => new { x.c, ca })
            .Where(x => x.ca.CompanyId == actor.CompanyId)
            .OrderBy(x => x.c.Id).Select(x => x.c).ToListAsync(ct);
    }
}
