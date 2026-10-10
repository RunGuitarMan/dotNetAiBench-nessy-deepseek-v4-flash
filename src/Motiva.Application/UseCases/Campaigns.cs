using Motiva.Application.Ports;
using Motiva.Domain;

namespace Motiva.Application.UseCases;

public sealed class CampaignUseCases(IUnitOfWork db, ICurrentActor current, ICompanyCalendar calendar, ITime time)
{
    private (Actor actor, bool isAdmin) Current()
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        return (actor, actor.IsAdmin);
    }

    private async Task<Campaign> GetOwnedAsync(Guid companyId, long id, CancellationToken ct)
    {
        return await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == companyId, ct)
            ?? throw MotivaException.NotFound("Кампания не найдена");
    }

    private async Task<bool> IsOwnerAsync(Guid companyId, long campaignId, int? masterId, CancellationToken ct)
    {
        if (masterId is null)
        {
            return false;
        }

        var owner = await db.Set<Campaign>().Where(c => c.Id == campaignId && c.CompanyId == companyId)
            .Join(db.Set<Employee>(), c => c.OwnerEmployeeId, e => e.Id, (c, e) => e)
            .FirstOrDefaultAsync(ct);
        return owner is not null && owner.MasterId == masterId;
    }

    public async Task<Campaign> CreateAsync(
        string code, string name, string? description, int season,
        DateTimeOffset start, DateTimeOffset end, Audience audience,
        IReadOnlyList<(long resourceId, bool allocationRequested)> resources, CancellationToken ct)
    {
        var (actor, isAdmin) = Current();
        ValidateBasics(code, name, description, start, end, audience, season);

        if (await db.Set<Campaign>().AnyAsync(c => c.CompanyId == actor.CompanyId && c.Season == season && c.Code.ToLower() == code.ToLower(), ct))
        {
            throw MotivaException.Conflict("Кампания с таким кодом уже существует в сезоне (B08)");
        }

        var campaign = new Campaign
        {
            CompanyId = actor.CompanyId,
            Code = code,
            Name = name,
            Description = description,
            Season = season,
            Start = start,
            End = end,
            Audience = audience,
        };
        db.Add(campaign);
        await db.SaveChangesAsync(ct);

        foreach (var (resourceId, allocationRequested) in resources)
        {
            await RequireResourceAsync(actor.CompanyId, resourceId, ct);
            db.Add(new CampaignResource { CampaignId = campaign.Id, ResourceId = resourceId, AllocationRequested = allocationRequested });
        }
        await db.SaveChangesAsync(ct);
        return campaign;
    }

    public async Task<Campaign> UpdateAsync(long id, CampaignPatchData patch, string? expectedEtag, CancellationToken ct)
    {
        var (actor, isAdmin) = Current();
        var campaign = await GetOwnedAsync(actor.CompanyId, id, ct);
        if (expectedEtag is null)
        {
            throw MotivaException.PreconditionRequired("Требуется If-Match");
        }

        if (campaign.Etag() != expectedEtag)
        {
            throw MotivaException.PreconditionFailed("ETag устарел");
        }

        if (patch.Status == CampaignState.Published)
        {
            return await PublishAsync(campaign, isAdmin, ct);
        }

        if (patch.Status == CampaignState.Archived)
        {
            if (campaign.State == CampaignState.Archived)
            {
                throw MotivaException.Conflict("Кампания уже в архиве");
            }

            if (campaign.State == CampaignState.Draft && !isAdmin)
            {
                throw MotivaException.Forbidden("Черновик архивирует только Admin");
            }

            campaign.State = CampaignState.Archived;
            db.Update(campaign);
            await db.SaveChangesAsync(ct);
            return campaign;
        }

        // Настройка полей.
        if (campaign.State != CampaignState.Draft)
        {
            // после публикации можно менять только name/description/audience/owner (B09f)
            if (patch.Code is not null || patch.Season is not null || patch.Start is not null || patch.End is not null || patch.Resources is not null)
            {
                throw MotivaException.Conflict("После публикации сезон/сроки/состав изменять нельзя (B09e)");
            }
        }
        if (patch.Code is not null)
        {
            if (!DomainRules.IsValidCode(patch.Code))
            {
                throw MotivaException.Validation("Некорректный код");
            }

            if (await db.Set<Campaign>().AnyAsync(c => c.CompanyId == actor.CompanyId && c.Season == campaign.Season && c.Code.ToLower() == patch.Code.ToLower() && c.Id != id, ct))
            {
                throw MotivaException.Conflict("Код уже используется");
            }

            campaign.Code = patch.Code;
        }
        if (patch.Name is not null)
        {
            if (!DomainRules.IsValidName(patch.Name))
            {
                throw MotivaException.Validation("Некорректное название");
            }

            campaign.Name = patch.Name;
        }
        if (patch.Description is not null)
        {
            campaign.Description = patch.Description;
        }

        if (patch.Season is int s)
        {
            campaign.Season = s;
        }

        if (patch.Start is DateTimeOffset st)
        {
            campaign.Start = st;
        }

        if (patch.End is DateTimeOffset en)
        {
            campaign.End = en;
        }

        if (patch.Audience is not null)
        {
            campaign.Audience = patch.Audience;
        }

        if (campaign.Start >= campaign.End)
        {
            throw MotivaException.Validation("start должен быть раньше end");
        }

        if (patch.OwnerEmployeeId is long ownerId)
        {
            if (!isAdmin)
            {
                throw MotivaException.Forbidden("Владельца назначает только Admin (B03)");
            }

            var owner = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == ownerId && e.CompanyId == actor.CompanyId, ct)
                ?? throw MotivaException.Validation("Владелец не найден");
            campaign.OwnerEmployeeId = owner.Id;
        }

        if (patch.Resources is not null && campaign.State == CampaignState.Draft)
        {
            var existing = await db.Set<CampaignResource>().Where(cr => cr.CampaignId == id).ToListAsync(ct);
            foreach (var e in existing)
            {
                db.Remove(e);
            }

            foreach (var r in patch.Resources)
            {
                await RequireResourceAsync(actor.CompanyId, r.resourceId, ct);
                db.Add(new CampaignResource { CampaignId = id, ResourceId = r.resourceId, AllocationRequested = r.allocationRequested });
            }
        }

        db.Update(campaign);
        await db.SaveChangesAsync(ct);
        return campaign;
    }

    private async Task<Campaign> PublishAsync(Campaign campaign, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
        {
            var owner = await IsOwnerAsync(campaign.CompanyId, campaign.Id, current.Actor!.MasterId, ct);
            if (!owner)
            {
                throw MotivaException.Forbidden("Публикует Admin либо владелец кампании");
            }
        }
        if (campaign.State != CampaignState.Draft)
        {
            throw MotivaException.Conflict("Публикуется только черновик (B09)");
        }

        await ValidatePublishAsync(campaign.Id, ct);
        campaign.State = CampaignState.Published;
        db.Update(campaign);
        await db.SaveChangesAsync(ct);
        return campaign;
    }

    private async Task ValidatePublishAsync(long campaignId, CancellationToken ct)
    {
        var campaign = await db.Set<Campaign>().FirstAsync(c => c.Id == campaignId, ct);
        var streams = await db.Set<CampaignStream>().Where(s => s.CampaignId == campaignId).ToListAsync(ct);
        if (streams.Count == 0)
        {
            throw MotivaException.Validation("Кампания должна содержать хотя бы один стрим");
        }

        foreach (var stream in streams)
        {
            if (stream.Status == ResourceStatus.Archived)
            {
                continue;
            }

            var tasks = await db.Set<CampaignTask>().Where(t => t.StreamId == stream.Id).ToListAsync(ct);
            if (tasks.Count == 0)
            {
                throw MotivaException.Validation($"Стрим '{stream.Code}' без заданий");
            }

            foreach (var task in tasks)
            {
                foreach (var reward in task.Rewards)
                {
                    if (!await db.Set<CampaignResource>().AnyAsync(cr => cr.CampaignId == campaignId && cr.ResourceId == reward.ResourceId, ct))
                    {
                        throw MotivaException.Validation($"Награда задания '{task.Code}' использует ресурс вне кампании (B12c)");
                    }
                }
            }
            foreach (var challenge in await db.Set<Challenge>().Where(c => c.StreamId == stream.Id).ToListAsync(ct))
            {
                if (challenge.Start >= challenge.End)
                {
                    throw MotivaException.Validation("Челлендж: start раньше end");
                }

                if (challenge.Start < campaign.Start || challenge.End > campaign.End)
                {
                    throw MotivaException.Validation("Челлендж должен быть внутри сроков кампании (B31)");
                }
            }
        }
    }

    public async Task DeleteDraftAsync(long id, string? expectedEtag, CancellationToken ct)
    {
        var (actor, isAdmin) = Current();
        var campaign = await GetOwnedAsync(actor.CompanyId, id, ct);
        if (campaign.State != CampaignState.Draft)
        {
            throw MotivaException.Conflict("Удаляется только черновик (B09i)");
        }

        if (expectedEtag is null)
        {
            throw MotivaException.PreconditionRequired("Требуется If-Match");
        }

        if (campaign.Etag() != expectedEtag)
        {
            throw MotivaException.PreconditionFailed("ETag устарел");
        }

        var hasHistory = await db.Set<Budget>().AnyAsync(b => b.CampaignId == id, ct)
            || await db.Set<Operation>().AnyAsync(o => o.CampaignId == id, ct);
        if (!hasHistory)
        {
            foreach (var s in await db.Set<CampaignStream>().Where(s => s.CampaignId == id).ToListAsync(ct))
            {
                foreach (var t in await db.Set<CampaignTask>().Where(t => t.StreamId == s.Id).ToListAsync(ct))
                {
                    foreach (var r in t.Rewards)
                    {
                        db.Remove(r);
                    }

                    db.Remove(t);
                }
                foreach (var m in await db.Set<Milestone>().Where(m => m.StreamId == s.Id).ToListAsync(ct))
                {
                    db.Remove(m);
                }

                foreach (var ch in await db.Set<Challenge>().Where(c => c.StreamId == s.Id).ToListAsync(ct))
                {
                    db.Remove(ch);
                }

                db.Remove(s);
            }
            foreach (var cr in await db.Set<CampaignResource>().Where(cr => cr.CampaignId == id).ToListAsync(ct))
            {
                db.Remove(cr);
            }

            db.Remove(campaign);
        }
        else
        {
            campaign.State = CampaignState.Archived; // логическое удаление, история сохраняется (D210)
            db.Update(campaign);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<Campaign> GetAsync(long id, CancellationToken ct)
    {
        var (actor, isAdmin) = Current();
        var campaign = await GetOwnedAsync(actor.CompanyId, id, ct);
        if (campaign.State == CampaignState.Draft)
        {
            var owner = await IsOwnerAsync(actor.CompanyId, id, actor.MasterId, ct);
            if (!isAdmin && !owner)
            {
                throw MotivaException.NotFound("Черновик не доступен (B09b)");
            }
        }
        else
        {
            // опубликованная: сотрудник видит только текущий сезон
            if (!isAdmin && !await IsOwnerAsync(actor.CompanyId, id, actor.MasterId, ct))
            {
                var visible = await VisibilityAsync(actor.CompanyId, id, actor.MasterId, ct);
                if (!visible)
                {
                    throw MotivaException.NotFound("Кампания недоступна");
                }
            }
        }
        return campaign;
    }

    public async Task<bool> VisibilityAsync(Guid companyId, long campaignId, int? masterId, CancellationToken ct)
    {
        if (masterId is null)
        {
            return false;
        }

        var campaign = await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == campaignId && c.CompanyId == companyId, ct);
        if (campaign is null || campaign.State != CampaignState.Published)
        {
            return false;
        }

        var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.CompanyId == companyId && e.MasterId == masterId, ct);
        if (employee is null || !employee.IsActive)
        {
            return false;
        }

        return AudienceRules.IsIncluded(campaign.Audience, employee.Tags);
    }

    private async Task RequireResourceAsync(Guid companyId, long resourceId, CancellationToken ct)
    {
        if (!await db.Set<Resource>().AnyAsync(r => r.Id == resourceId && r.CompanyId == companyId, ct))
        {
            throw MotivaException.Validation("Ресурс не принадлежит компании");
        }
    }

    private static void ValidateBasics(string code, string name, string? description, DateTimeOffset start, DateTimeOffset end, Audience audience, int season)
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

        if (start >= end)
        {
            throw MotivaException.Validation("start должен быть раньше end");
        }

        ValidateAudience(audience);
    }

    public static void ValidateAudience(Audience audience)
    {
        foreach (var tag in audience.Any.Concat(audience.All).Concat(audience.None))
        {
            if (!DomainRules.IsValidTag(tag))
            {
                throw MotivaException.Validation("Некорректный тег аудитории");
            }
        }
    }
}

/// <summary>Данные PATCH кампании (T04).</summary>
public sealed record CampaignPatchData(
    string? Code, string? Name, string? Description, int? Season,
    DateTimeOffset? Start, DateTimeOffset? End, Audience? Audience,
    IReadOnlyList<(long resourceId, bool allocationRequested)>? Resources,
    long? OwnerEmployeeId, CampaignState? Status);
