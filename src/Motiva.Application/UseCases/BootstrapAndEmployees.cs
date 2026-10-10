using Motiva.Application.Ports;
using Motiva.Domain;

namespace Motiva.Application.UseCases;

/// <summary>Начальное создание компаний и активного Admin через доверенные use cases (T03).</summary>
public sealed class BootstrapUseCases(IUnitOfWork db)
{
    /// <summary>Создать компанию и активного администратора (bootstrap).</summary>
    public async Task<Company> CreateCompanyAsync(string name, int adminMasterId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw MotivaException.Validation("Некорректное название компании");
        }

        if (!DomainRules.IsValidMasterId(adminMasterId))
        {
            throw MotivaException.Validation("Некорректный masterId администратора");
        }

        var company = new Company { Id = Guid.NewGuid(), Name = name };
        db.Add(company);

        var admin = new Employee
        {
            CompanyId = company.Id,
            MasterId = adminMasterId,
            IsActive = true,
        };
        db.Add(admin);
        await db.SaveChangesAsync(ct);

        // ровно один кошелёк (B01d/B22)
        db.Add(new Wallet { EmployeeId = admin.Id });
        await db.SaveChangesAsync(ct);
        return company;
    }
}

public sealed class EmployeeUseCases(IUnitOfWork db, ICurrentActor current)
{
    public async Task<Employee> CreateEmployeeAsync(int masterId, IReadOnlyCollection<string> tags, CancellationToken ct)
    {
        var actor = RequireAdmin();
        if (!DomainRules.IsValidMasterId(masterId))
        {
            throw MotivaException.Validation("Некорректный masterId");
        }

        if (tags.Any(t => !DomainRules.IsValidTag(t)))
        {
            throw MotivaException.Validation("Некорректный тег аудитории");
        }

        if (await db.Set<Employee>().AnyAsync(e => e.CompanyId == actor.CompanyId && e.MasterId == masterId, ct))
        {
            throw MotivaException.Conflict("Сотрудник с таким masterId уже существует (B01)");
        }

        var employee = new Employee
        {
            CompanyId = actor.CompanyId,
            MasterId = masterId,
            IsActive = true,
            Tags = tags.ToList(),
        };
        db.Add(employee);
        await db.SaveChangesAsync(ct);
        db.Add(new Wallet { EmployeeId = employee.Id });
        await db.SaveChangesAsync(ct);
        return employee;
    }

    public async Task<Employee> UpdateEmployeeAsync(long employeeId, IReadOnlyCollection<string>? tags, bool? isActive, string? expectedEtag, CancellationToken ct)
    {
        var actor = RequireAdmin();
        var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == actor.CompanyId, ct)
            ?? throw MotivaException.NotFound("Сотрудник не найден");
        if (expectedEtag is not null && employee.Etag() != expectedEtag)
        {
            throw MotivaException.PreconditionFailed("ETag устарел");
        }

        if (tags is not null)
        {
            if (tags.Any(t => !DomainRules.IsValidTag(t)))
            {
                throw MotivaException.Validation("Некорректный тег аудитории");
            }

            employee.Tags = tags.ToList();
        }
        if (isActive is not null)
        {
            employee.IsActive = isActive.Value;
        }

        db.Update(employee);
        await db.SaveChangesAsync(ct);
        return employee;
    }

    public async Task<Employee> GetEmployeeAsync(long employeeId, CancellationToken ct)
    {
        var actor = RequireAdmin();
        return await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == actor.CompanyId, ct)
            ?? throw MotivaException.NotFound("Сотрудник не найден");
    }

    public async Task<List<Employee>> ListEmployeesAsync(CancellationToken ct)
    {
        var actor = RequireAdmin();
        return await db.Set<Employee>().Where(e => e.CompanyId == actor.CompanyId)
            .OrderBy(e => e.Id).ToListAsync(ct);
    }

    private Actor RequireAdmin()
    {
        var actor = current.Actor
            ?? throw MotivaException.Forbidden("Требуется аутентификация");
        if (!actor.IsAdmin)
        {
            throw MotivaException.Forbidden("Требуется роль Admin");
        }

        return actor;
    }
}

public static class EntityEtag
{
    /// <summary>Strong ETag по версии/моменту изменения объекта (T04).</summary>
    public static string Etag(this object entity)
    {
        // детерминированный strong ETag; для перегрузки объекты с версией дают отдельный ETag
        var key = entity.GetType().Name + ":" + System.Text.Json.JsonSerializer.Serialize(entity);
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return "\"" + Convert.ToHexStringLower(bytes)[..16] + "\"";
    }
}
