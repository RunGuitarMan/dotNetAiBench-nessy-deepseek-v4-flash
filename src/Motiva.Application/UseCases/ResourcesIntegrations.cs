using Motiva.Application.Ports;
using Motiva.Domain;

namespace Motiva.Application.UseCases;

public sealed class ResourceUseCases(IUnitOfWork db, ICurrentActor current)
{
    private Actor RequireAdmin()
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        if (!actor.IsAdmin)
        {
            throw MotivaException.Forbidden("Требуется роль Admin");
        }

        return actor;
    }

    public async Task<Resource> CreateResourceAsync(string code, string name, CancellationToken ct)
    {
        var actor = RequireAdmin();
        if (!DomainRules.IsValidCode(code))
        {
            throw MotivaException.Validation("Некорректный код ресурса");
        }

        if (!DomainRules.IsValidName(name))
        {
            throw MotivaException.Validation("Некорректное название");
        }

        if (await db.Set<Resource>().AnyAsync(r => r.CompanyId == actor.CompanyId && r.Code.ToLower() == code.ToLower(), ct))
        {
            throw MotivaException.Conflict("Ресурс с таким кодом уже существует (B06)");
        }

        var resource = new Resource { CompanyId = actor.CompanyId, Code = code, Name = name };
        db.Add(resource);
        await db.SaveChangesAsync(ct);
        return resource;
    }

    public async Task<Resource> UpdateResourceAsync(long resourceId, string? name, ResourceStatus? status, string? expectedEtag, CancellationToken ct)
    {
        var actor = RequireAdmin();
        var resource = await db.Set<Resource>().FirstOrDefaultAsync(r => r.Id == resourceId && r.CompanyId == actor.CompanyId, ct)
            ?? throw MotivaException.NotFound("Ресурс не найден");
        if (expectedEtag is not null && resource.Etag() != expectedEtag)
        {
            throw MotivaException.PreconditionFailed("ETag устарел");
        }

        if (name is not null)
        {
            if (!DomainRules.IsValidName(name))
            {
                throw MotivaException.Validation("Некорректное название");
            }

            resource.Name = name;
        }
        if (status == ResourceStatus.Archived)
        {
            if (resource.Status != ResourceStatus.Archived)
            {
                resource.Status = ResourceStatus.Archived; // необратимо (B07)
            }
        }
        db.Update(resource);
        await db.SaveChangesAsync(ct);
        return resource;
    }

    public async Task<Resource> GetResourceAsync(long resourceId, CancellationToken ct)
    {
        var actor = RequireAdmin();
        return await db.Set<Resource>().FirstOrDefaultAsync(r => r.Id == resourceId && r.CompanyId == actor.CompanyId, ct)
            ?? throw MotivaException.NotFound("Ресурс не найден");
    }

    public async Task<List<Resource>> ListResourcesAsync(CancellationToken ct)
    {
        var actor = RequireAdmin();
        return await db.Set<Resource>().Where(r => r.CompanyId == actor.CompanyId).OrderBy(r => r.Id).ToListAsync(ct);
    }
}

public sealed class IntegrationUseCases(IUnitOfWork db, ICurrentActor current, ITime time)
{
    private Actor RequireAdmin()
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        if (!actor.IsAdmin)
        {
            throw MotivaException.Forbidden("Требуется роль Admin");
        }

        return actor;
    }

    public async Task<Integration> CreateIntegrationAsync(string name, string serviceSub, CancellationToken ct)
    {
        var actor = RequireAdmin();
        if (!DomainRules.IsValidName(name))
        {
            throw MotivaException.Validation("Некорректное название");
        }

        if (string.IsNullOrWhiteSpace(serviceSub) || serviceSub.Length > 200)
        {
            throw MotivaException.Validation("Некорректный serviceSub");
        }

        var integration = new Integration { CompanyId = actor.CompanyId, Name = name, ServiceSub = serviceSub };
        db.Add(integration);
        await db.SaveChangesAsync(ct);
        return integration;
    }

    public async Task<List<Integration>> ListIntegrationsAsync(CancellationToken ct)
    {
        var actor = RequireAdmin();
        return await db.Set<Integration>().Where(i => i.CompanyId == actor.CompanyId).OrderBy(i => i.Id).ToListAsync(ct);
    }

    public async Task<Integration> GetIntegrationAsync(long integrationId, CancellationToken ct)
    {
        var actor = RequireAdmin();
        return await db.Set<Integration>().FirstOrDefaultAsync(i => i.Id == integrationId && i.CompanyId == actor.CompanyId, ct)
            ?? throw MotivaException.NotFound("Интеграция не найдена");
    }

    /// <summary>ETag текущего набора grants (I201) — сильный, и для пустого набора.</summary>
    public async Task<string> GrantsEtagAsync(long integrationId, CancellationToken ct)
    {
        var actor = RequireAdmin();
        var grants = await db.Set<IntegrationGrant>()
            .Where(g => g.IntegrationId == integrationId && db.Set<Integration>().Any(i => i.Id == integrationId && i.CompanyId == actor.CompanyId))
            .OrderBy(g => g.Id).ToListAsync(ct);
        return SetEtag(grants);
    }

    public async Task<List<IntegrationGrant>> ListGrantsAsync(long integrationId, CancellationToken ct)
    {
        var actor = RequireAdmin();
        await EnsureIntegration(integrationId, actor.CompanyId, ct);
        return await db.Set<IntegrationGrant>().Where(g => g.IntegrationId == integrationId).OrderBy(g => g.Id).ToListAsync(ct);
    }

    public async Task<(List<IntegrationGrant> grants, string etag)> ReplaceGrantsAsync(long integrationId, IReadOnlyList<IntegrationGrant> grants, string? expectedEtag, CancellationToken ct)
    {
        var actor = RequireAdmin();
        await EnsureIntegration(integrationId, actor.CompanyId, ct);
        var current = await db.Set<IntegrationGrant>().Where(g => g.IntegrationId == integrationId).OrderBy(g => g.Id).ToListAsync(ct);
        if (expectedEtag is null)
        {
            throw MotivaException.PreconditionRequired("Требуется If-Match");
        }

        if (SetEtag(current) != expectedEtag)
        {
            throw MotivaException.PreconditionFailed("ETag устарел");
        }

        // Валидация областей grant (UT-05/D210) + принадлежность объектов компании.
        foreach (var g in grants)
        {
            GrantRules.Validate(g.Kind, g.CampaignId, g.ResourceId, g.PurchaseSystemId);
            await ValidateScopeAsync(actor.CompanyId, g, ct);
        }

        foreach (var g in current)
        {
            db.Remove(g);
        }

        foreach (var g in grants)
        {
            db.Add(new IntegrationGrant
            {
                IntegrationId = integrationId,
                Kind = g.Kind,
                CampaignId = g.CampaignId,
                ResourceId = g.ResourceId,
                PurchaseSystemId = g.PurchaseSystemId,
            });
        }
        await db.SaveChangesAsync(ct);

        db.Add(new AuditEntry
        {
            CompanyId = actor.CompanyId,
            Actor = actor.InitiatorKey,
            Target = $"integration:{integrationId}",
            Action = AuditAction.PermissionChange,
            Details = new() { ["count"] = grants.Count.ToString() },
            OccurredAt = time.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        var result = await db.Set<IntegrationGrant>().Where(g => g.IntegrationId == integrationId).OrderBy(g => g.Id).ToListAsync(ct);
        return (result, SetEtag(result));
    }

    private async Task EnsureIntegration(long integrationId, Guid companyId, CancellationToken ct)
    {
        _ = await db.Set<Integration>().FirstOrDefaultAsync(i => i.Id == integrationId && i.CompanyId == companyId, ct)
            ?? throw MotivaException.NotFound("Интеграция не найдена");
    }

    private async Task ValidateScopeAsync(Guid companyId, IntegrationGrant g, CancellationToken ct)
    {
        if (g.CampaignId is long cid && !await db.Set<Campaign>().AnyAsync(c => c.Id == cid && c.CompanyId == companyId, ct))
        {
            throw MotivaException.Validation("Кампания не принадлежит компании");
        }

        if (g.ResourceId is long rid && !await db.Set<Resource>().AnyAsync(r => r.Id == rid && r.CompanyId == companyId, ct))
        {
            throw MotivaException.Validation("Ресурс не принадлежит компании");
        }

        if (g.PurchaseSystemId is long pid && !await db.Set<PurchaseSystem>().AnyAsync(p => p.Id == pid && p.CompanyId == companyId, ct))
        {
            throw MotivaException.Validation("Система покупок не принадлежит компании");
        }
    }

    private static string SetEtag(IEnumerable<IntegrationGrant> grants)
    {
        var key = string.Join("|", grants.OrderBy(g => g.Id).Select(g =>
            $"{g.Kind}:{g.CampaignId}:{g.ResourceId}:{g.PurchaseSystemId}"));
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return "\"" + Convert.ToHexStringLower(bytes)[..16] + "\"";
    }
}

public sealed class PurchaseSystemUseCases(IUnitOfWork db, ICurrentActor current)
{
    private Actor RequireAdmin()
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        if (!actor.IsAdmin)
        {
            throw MotivaException.Forbidden("Требуется роль Admin");
        }

        return actor;
    }

    public async Task<PurchaseSystem> CreateAsync(string name, CancellationToken ct)
    {
        var actor = RequireAdmin();
        if (!DomainRules.IsValidName(name))
        {
            throw MotivaException.Validation("Некорректное название");
        }

        var system = new PurchaseSystem { CompanyId = actor.CompanyId, Name = name };
        db.Add(system);
        await db.SaveChangesAsync(ct);
        return system;
    }

    public async Task<List<PurchaseSystem>> ListAsync(CancellationToken ct)
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        return await db.Set<PurchaseSystem>().Where(p => p.CompanyId == actor.CompanyId).OrderBy(p => p.Id).ToListAsync(ct);
    }

    public async Task<PurchaseSystem> GetAsync(long id, CancellationToken ct)
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        return await db.Set<PurchaseSystem>().FirstOrDefaultAsync(p => p.Id == id && p.CompanyId == actor.CompanyId, ct)
            ?? throw MotivaException.NotFound("Система покупок не найдена");
    }

    public async Task<PurchaseSystem> UpdateNameAsync(long id, string name, string? expectedEtag, CancellationToken ct)
    {
        var actor = RequireAdmin();
        if (!DomainRules.IsValidName(name))
        {
            throw MotivaException.Validation("Некорректное название");
        }

        var system = await GetAsync(id, ct);
        if (expectedEtag is null)
        {
            throw MotivaException.PreconditionRequired("Требуется If-Match");
        }

        if (system.Etag() != expectedEtag)
        {
            throw MotivaException.PreconditionFailed("ETag устарел");
        }

        system.Name = name;
        db.Update(system);
        await db.SaveChangesAsync(ct);
        return system;
    }

    public async Task<List<long>> SetResourcesAsync(long id, IReadOnlyCollection<long> resourceIds, string? expectedEtag, CancellationToken ct)
    {
        var actor = RequireAdmin();
        var system = await GetAsync(id, ct);
        if (expectedEtag is null)
        {
            throw MotivaException.PreconditionRequired("Требуется If-Match");
        }

        if (system.Etag() != expectedEtag)
        {
            throw MotivaException.PreconditionFailed("ETag устарел");
        }

        foreach (var rid in resourceIds)
        {
            if (!await db.Set<Resource>().AnyAsync(r => r.Id == rid && r.CompanyId == actor.CompanyId, ct))
            {
                throw MotivaException.Validation("Ресурс не принадлежит компании");
            }
        }
        system.ResourceIds = resourceIds.Distinct().ToList();
        db.Update(system);
        await db.SaveChangesAsync(ct);
        return system.ResourceIds;
    }
}
