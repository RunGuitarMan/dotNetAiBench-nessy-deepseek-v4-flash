using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Motiva.Application.Ports;

namespace Motiva.Infrastructure;

public sealed class UnitOfWork(MotivaDbContext db) : IUnitOfWork
{
    private bool _disposed;

    public Guid TenantCompanyId { get; private set; }

    public IQueryable<T> Set<T>() where T : class => db.Set<T>();

    public void Add<T>(T entity) where T : class => db.Add(entity);
    public void Update<T>(T entity) where T : class => db.Update(entity);
    public void Remove<T>(T entity) where T : class => db.Remove(entity);

    public Task<int> SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public async Task<ITransaction> BeginTransactionAsync(IsolationLevel level, CancellationToken ct = default)
    {
        var tx = await db.Database.BeginTransactionAsync(level, ct);
        return new EfTransaction(tx);
    }

    public async Task<T> ExecuteSerializableAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                var result = await operation(ct);
                await tx.CommitAsync(ct);
                return result;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        });
    }

    public async Task ExecuteSerializableAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                await operation(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await db.DisposeAsync();
    }

    private sealed class EfTransaction(IDbContextTransaction tx) : ITransaction
    {
        public Task CommitAsync(CancellationToken ct = default) => tx.CommitAsync(ct);
        public Task RollbackAsync(CancellationToken ct = default) => tx.RollbackAsync(ct);
        public async ValueTask DisposeAsync() => await tx.DisposeAsync();
    }
}
