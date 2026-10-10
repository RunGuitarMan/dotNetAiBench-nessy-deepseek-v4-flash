using Motiva.Application.Ports;
using Motiva.Domain;

namespace Motiva.Application.UseCases;

public sealed class AchievementUseCases(IUnitOfWork db, ICurrentActor current)
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

    public async Task<Achievement> CreateAsync(string code, string name, string? description, CancellationToken ct)
    {
        var actor = RequireAdmin();
        if (!DomainRules.IsValidCode(code))
        {
            throw MotivaException.Validation("Некорректный код достижения");
        }

        if (!DomainRules.IsValidName(name))
        {
            throw MotivaException.Validation("Некорректное название");
        }

        if (!DomainRules.IsValidDescription(description))
        {
            throw MotivaException.Validation("Некорректное описание");
        }

        if (await db.Set<Achievement>().AnyAsync(a => a.CompanyId == actor.CompanyId && a.Code.ToLower() == code.ToLower(), ct))
        {
            throw MotivaException.Conflict("Достижение с таким кодом уже существует");
        }

        var achievement = new Achievement { CompanyId = actor.CompanyId, Code = code, Name = name, Description = description };
        db.Add(achievement);
        await db.SaveChangesAsync(ct);
        return achievement;
    }

    public async Task<Achievement> UpdateAsync(long id, string? name, string? description, string? expectedEtag, CancellationToken ct)
    {
        var actor = RequireAdmin();
        var achievement = await db.Set<Achievement>().FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == actor.CompanyId, ct)
            ?? throw MotivaException.NotFound("Достижение не найдено");
        if (expectedEtag is null)
        {
            throw MotivaException.PreconditionRequired("Требуется If-Match");
        }

        if (achievement.Etag() != expectedEtag)
        {
            throw MotivaException.PreconditionFailed("ETag устарел");
        }

        if (name is not null)
        {
            achievement.Name = name;
        }

        if (description is not null)
        {
            achievement.Description = description;
        }

        db.Update(achievement);
        await db.SaveChangesAsync(ct);
        return achievement;
    }

    public async Task<Achievement> GetAsync(long id, CancellationToken ct)
    {
        var actor = RequireAdmin();
        return await db.Set<Achievement>().FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == actor.CompanyId, ct)
            ?? throw MotivaException.NotFound("Достижение не найдено");
    }

    public async Task<List<Achievement>> ListAsync(CancellationToken ct)
    {
        var actor = RequireAdmin();
        return await db.Set<Achievement>().Where(a => a.CompanyId == actor.CompanyId).OrderBy(a => a.Id).ToListAsync(ct);
    }
}
