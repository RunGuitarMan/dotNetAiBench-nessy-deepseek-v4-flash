using Motiva.Application.Ports;
using Motiva.Domain;

namespace Motiva.Application.UseCases;

public sealed class WalletReadUseCases(IUnitOfWork db, ICurrentActor current)
{
    private (Actor actor, Employee? employee) Self()
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        return (actor, null);
    }

    private async Task<Employee> RequireSelfEmployeeAsync(Actor actor, CancellationToken ct)
    {
        if (actor.Type != ActorType.User || actor.MasterId is null)
        {
            throw MotivaException.Forbidden("Только сотрудник");
        }

        return await db.Set<Employee>().FirstOrDefaultAsync(e => e.CompanyId == actor.CompanyId && e.MasterId == actor.MasterId, ct)
            ?? throw MotivaException.NotFound("Профиль не найден");
    }

    public async Task<WalletView> GetWalletAsync(CancellationToken ct)
    {
        var (actor, _) = Self();
        var employee = await RequireSelfEmployeeAsync(actor, ct);
        var wallet = await db.Set<Wallet>().FirstAsync(w => w.EmployeeId == employee.Id, ct);
        var balances = await db.Set<WalletBalance>().Where(b => b.WalletId == wallet.Id).ToListAsync(ct);
        var resources = await db.Set<Resource>().Where(r => r.CompanyId == actor.CompanyId).ToDictionaryAsync(r => r.Id, r => r.Code, ct);
        return new WalletView(employee.Id, balances
            .Select(b => new BalanceView(b.ResourceId, resources.GetValueOrDefault(b.ResourceId) ?? "", b.Balance))
            .OrderBy(b => b.ResourceId).ToList());
    }

    public async Task<List<WalletMovement>> ListMovementsAsync(long? resourceId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var (actor, _) = Self();
        var employee = await RequireSelfEmployeeAsync(actor, ct);
        var wallet = await db.Set<Wallet>().FirstAsync(w => w.EmployeeId == employee.Id, ct);
        var q = db.Set<WalletMovement>().Where(m => m.WalletId == wallet.Id);
        if (resourceId is long rid)
        {
            q = q.Where(m => m.ResourceId == rid);
        }

        if (from is DateTimeOffset f)
        {
            q = q.Where(m => m.OccurredAt >= f);
        }

        if (to is DateTimeOffset t)
        {
            q = q.Where(m => m.OccurredAt < t);
        }

        return await q.OrderBy(m => m.Id).ToListAsync(ct);
    }

    public async Task<List<Operation>> ListWalletOperationsAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var (actor, _) = Self();
        var employee = await RequireSelfEmployeeAsync(actor, ct);
        var q = db.Set<Operation>().Where(o => o.CompanyId == actor.CompanyId && o.EmployeeId == employee.Id);
        if (from is DateTimeOffset f)
        {
            q = q.Where(o => o.OccurredAt >= f);
        }

        if (to is DateTimeOffset t)
        {
            q = q.Where(o => o.OccurredAt < t);
        }

        return await q.OrderBy(o => o.Id).ToListAsync(ct);
    }

    public async Task<Operation> GetOperationAsync(long operationId, CancellationToken ct)
    {
        var (actor, _) = Self();
        var op = await db.Set<Operation>().FirstOrDefaultAsync(o => o.Id == operationId && o.CompanyId == actor.CompanyId, ct)
            ?? throw MotivaException.NotFound("Операция не найдена");
        if (actor.IsAdmin)
        {
            return op;
        }

        if (actor.Type == ActorType.User)
        {
            var employee = await RequireSelfEmployeeAsync(actor, ct);
            if (op.EmployeeId == employee.Id)
            {
                return op;
            }
        }
        if (actor.Type == ActorType.Service)
        {
            // сервис видит только свою историю (B04c)
            if (op.InitiatorKey == actor.InitiatorKey)
            {
                return op;
            }
        }
        throw MotivaException.NotFound("Операция недоступна");
    }

    /// <summary>Своя история операций сервиса (B04c).</summary>
    public async Task<List<Operation>> ListIntegrationOperationsAsync(CancellationToken ct)
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        if (actor.Type != ActorType.Service)
        {
            throw MotivaException.Forbidden("Только сервис");
        }

        return await db.Set<Operation>().Where(o => o.CompanyId == actor.CompanyId && o.InitiatorKey == actor.InitiatorKey)
            .OrderBy(o => o.Id).ToListAsync(ct);
    }
}

public sealed record WalletView(long EmployeeId, List<BalanceView> Balances);
public sealed record BalanceView(long ResourceId, string Code, long Balance);
public sealed record MovementView(long Id, long OperationId, long ResourceId, long Delta, long BalanceAfter, DateTimeOffset OccurredAt, OperationKind Kind, long? CampaignId, long? OriginalOperationId);
