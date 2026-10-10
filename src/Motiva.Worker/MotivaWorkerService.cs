using Microsoft.EntityFrameworkCore;
using Motiva.Application.Ports;
using Motiva.Application.UseCases;
using Motiva.Domain;
using Motiva.Infrastructure;

namespace Motiva.Worker;

/// <summary>
/// Фоновые циклы: формирование выписок, финализация челленджей, cleanup S3 (T07/D208).
/// Сериализация корректности — в PostgreSQL (D208), не process-local lock.
/// </summary>
public sealed class MotivaWorkerService(
    IServiceScopeFactory scopeFactory,
    ILogger<MotivaWorkerService> logger) : BackgroundService
{
    private readonly string _workerId = "worker-" + Guid.NewGuid().ToString("N")[..8];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var scope = scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MotivaDbContext>>();
            try
            {
                await ProcessStatementsAsync(scope.ServiceProvider, stoppingToken);
                await FinalizeChallengesAsync(scope.ServiceProvider, stoppingToken);
                await CleanupDeletedAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Фоновая итерация не выполнена");
            }
        }
    }

    private async Task ProcessStatementsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var generator = sp.GetRequiredService<IStatementGenerator>();
        await using var db = await sp.GetRequiredService<IDbContextFactory<MotivaDbContext>>().CreateDbContextAsync(ct);
        var ids = await db.Statements
            .Where(s => s.State == StatementState.Pending || s.State == StatementState.Forming)
            .OrderBy(s => s.Id).Take(20).Select(s => s.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            try { await generator.GenerateAsync(id, _workerId, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Формирование выписки {id} не удалось", id); }
        }
    }

    private async Task FinalizeChallengesAsync(IServiceProvider sp, CancellationToken ct)
    {
        var useCases = sp.GetRequiredService<LeaderboardUseCases>();
        await using var db = await sp.GetRequiredService<IDbContextFactory<MotivaDbContext>>().CreateDbContextAsync(ct);
        var now = sp.GetRequiredService<ITime>().UtcNow;
        var ids = await db.Challenges.Where(c => c.State == ChallengeState.Open && c.End <= now)
            .OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            await useCases.FinalizeChallengeAsync(id, ct);
        }
    }

    private async Task CleanupDeletedAsync(IServiceProvider sp, CancellationToken ct)
    {
        var storage = sp.GetRequiredService<IObjectStorage>();
        await using var db = await sp.GetRequiredService<IDbContextFactory<MotivaDbContext>>().CreateDbContextAsync(ct);
        var deleted = await db.Statements
            .Where(s => s.State == StatementState.Deleted && s.FileRef != null)
            .ToListAsync(ct);
        foreach (var s in deleted)
        {
            try
            {
                await storage.DeleteAsync(storage.Bucket(s.CompanyId), s.FileRef!, ct);
                s.FileRef = null;
                db.Update(s);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Cleanup выписки {id} не удался", s.Id); }
        }
    }
}
