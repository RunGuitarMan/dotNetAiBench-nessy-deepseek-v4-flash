using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Motiva.Application.Ports;
using Motiva.Domain;

namespace Motiva.Infrastructure;

/// <summary>Потоковый генератор CSV выписки (T07).</summary>
public static class CsvStatementWriter
{
    public const string Header = "operationId,masterId,resourceCode,amount,kind,campaignId,originalOperationId,createdAtUtc";

    /// <summary>Записать строки движения в StreamWriter (потоково, ограниченная память).</summary>
    public static async Task WriteAsync(StreamWriter sw, IEnumerable<CsvRow> rows, CancellationToken ct)
    {
        await sw.WriteLineAsync(Header);
        foreach (var r in rows.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.OperationId).ThenBy(r => r.ResourceCode, StringComparer.Ordinal))
        {
            await sw.WriteAsync(Escape(r.OperationId.ToString()));
            await sw.WriteAsync(',');
            await sw.WriteAsync(Escape(r.MasterId.ToString()));
            await sw.WriteAsync(',');
            await sw.WriteAsync(Escape(r.ResourceCode.ToUpperInvariant()));
            await sw.WriteAsync(',');
            await sw.WriteAsync(r.Amount.ToString(CultureInfo.InvariantCulture));
            await sw.WriteAsync(',');
            await sw.WriteAsync(Escape(r.Kind));
            await sw.WriteAsync(',');
            await sw.WriteAsync(r.CampaignId?.ToString() ?? string.Empty);
            await sw.WriteAsync(',');
            await sw.WriteAsync(r.OriginalOperationId?.ToString() ?? string.Empty);
            await sw.WriteAsync(',');
            await sw.WriteAsync(r.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await sw.WriteLineAsync();
        }
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}

public sealed record CsvRow(long OperationId, int MasterId, string ResourceCode, long Amount, string Kind, long? CampaignId, long? OriginalOperationId, DateTimeOffset CreatedAtUtc);

/// <summary>Сервис формирования выписки: снимок (B36), CSV (T07), S3 (put-if-absent), CAS Ready (D208).</summary>
public sealed class StatementGeneratorService(
    IDbContextFactory<MotivaDbContext> contextFactory,
    IObjectStorage storage,
    TimeProvider time) : IStatementGenerator
{
    public async Task<bool> GenerateAsync(long statementId, string workerId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        var statement = await db.Statements.FirstOrDefaultAsync(s => s.Id == statementId, ct);
        if (statement is null || statement.State is StatementState.Deleted or StatementState.Ready)
        {
            return false;
        }

        // Взять/подтвердить владение попыткой (lease).
        var attempt = await db.StatementAttempts.FirstOrDefaultAsync(a => a.StatementId == statementId, ct);
        var now = time.GetUtcNow();
        if (attempt is null)
        {
            db.StatementAttempts.Add(new StatementAttempt { StatementId = statementId, OwnerWorkerId = workerId, LeaseExpiresAt = now.AddMinutes(2) });
        }
        else if (attempt.LeaseExpiresAt > now && attempt.OwnerWorkerId != workerId)
        {
            return false; // занято другим worker
        }
        else
        {
            attempt.OwnerWorkerId = workerId;
            attempt.LeaseExpiresAt = now.AddMinutes(2);
            db.Update(attempt);
        }
        await db.SaveChangesAsync(ct);

        // Снимок состава при первом успешном начале формирования (B36) — MVCC в Repeatable Read.
        Guid snapshotId;
        List<long> movementIds;
        var data = await db.StatementDatas.FirstOrDefaultAsync(d => d.StatementId == statementId, ct);
        if (data is null)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var movements = await SelectMovementsAsync(db, statement, ct);
            movementIds = movements.Select(m => m.Id).ToList();
            snapshotId = Guid.NewGuid();
            db.StatementDatas.Add(new StatementData
            {
                StatementId = statementId,
                SnapshotId = snapshotId,
                MovementIdsJson = System.Text.Json.JsonSerializer.Serialize(movementIds),
            });
            statement.State = StatementState.Forming; // единственный переход, фиксирующий снимок
            db.Update(statement);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        else
        {
            snapshotId = data.SnapshotId;
            movementIds = System.Text.Json.JsonSerializer.Deserialize<List<long>>(data.MovementIdsJson) ?? new();
        }

        // Потоковое формирование CSV из зафиксированного состава.
        var key = snapshotId.ToString("N");
        var bucket = storage.Bucket(statement.CompanyId);
        var exists = await storage.ExistsAsync(bucket, key, ct);

        if (!exists)
        {
            using var buffer = new MemoryStream();
            await using (var sw = new StreamWriter(buffer, new UTF8Encoding(false), 1024, leaveOpen: true))
            {
                var rows = await LoadRowsAsync(db, statement, movementIds, ct);
                await CsvStatementWriter.WriteAsync(sw, rows, ct);
                await sw.FlushAsync(ct);
            }
            buffer.Position = 0;
            // conditional write: не перезаписываем существующий проверенный объект (D208/FT-159)
            await storage.PutIfAbsentAsync(bucket, key, buffer, "text/csv; charset=utf-8", ct);
        }

        // CAS Ready: только при state=forming и своём владении (D208). Атомарно через SQL.
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE statements
            SET "State" = {StatementState.Ready}, "FileRef" = {key}
            WHERE "Id" = {statementId} AND "State" = {StatementState.Forming}
              AND (SELECT "OwnerWorkerId" FROM statement_attempts WHERE "StatementId" = {statementId}) = {workerId}
            """, ct);

        return updated > 0;
    }

    private async Task<List<Motiva.Domain.WalletMovement>> SelectMovementsAsync(MotivaDbContext db, Statement statement, CancellationToken ct)
    {
        var q = db.WalletMovements.AsQueryable();
        if (statement.Scope == StatementScope.Employee && statement.EmployeeId is long eid)
        {
            var walletId = await db.Wallets.Where(w => w.EmployeeId == eid).Select(w => w.Id).FirstAsync(ct);
            q = q.Where(m => m.WalletId == walletId);
        }
        if (statement.Scope == StatementScope.Company)
        {
            // все кошельки компании
            var walletIds = await db.Employees.Where(e => e.CompanyId == statement.CompanyId)
                .Join(db.Wallets, e => e.Id, w => w.EmployeeId, (e, w) => w.Id).ToListAsync(ct);
            q = q.Where(m => walletIds.Contains(m.WalletId));
        }
        q = q.Where(m => m.OccurredAt >= statement.From && m.OccurredAt < statement.To);
        if (statement.ResourceId is long rid)
        {
            q = q.Where(m => m.ResourceId == rid);
        }

        return await q.OrderBy(m => m.Id).ToListAsync(ct);
    }

    private async Task<List<CsvRow>> LoadRowsAsync(MotivaDbContext db, Statement statement, List<long> movementIds, CancellationToken ct)
    {
        if (movementIds.Count == 0)
        {
            return new List<CsvRow>();
        }

        var movements = await db.WalletMovements.Where(m => movementIds.Contains(m.Id)).ToListAsync(ct);
        var resourceCodes = await db.Resources.Where(r => r.CompanyId == statement.CompanyId).ToDictionaryAsync(r => r.Id, r => r.Code, ct);
        var masterIds = new Dictionary<long, int>();
        var walletIds = movements.Select(m => m.WalletId).Distinct().ToList();
        var walletToEmployee = await db.Wallets.Where(w => walletIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.EmployeeId, ct);
        var employeeIds = walletToEmployee.Values.Distinct().ToList();
        var empRows = await db.Employees.Where(e => employeeIds.Contains(e.Id)).ToListAsync(ct);
        foreach (var e in empRows)
        {
            masterIds[e.Id] = e.MasterId;
        }

        var result = new List<CsvRow>();
        foreach (var m in movements)
        {
            var employeeId = walletToEmployee.GetValueOrDefault(m.WalletId);
            result.Add(new CsvRow(
                m.OperationId,
                masterIds.GetValueOrDefault(employeeId),
                resourceCodes.GetValueOrDefault(m.ResourceId) ?? string.Empty,
                m.Delta,
                m.Kind.ToString(),
                m.CampaignId,
                m.OriginalOperationId,
                m.OccurredAt));
        }
        return result;
    }
}
