using Motiva.Application.Ports;
using Motiva.Domain;

namespace Motiva.Application.UseCases;

public sealed class LeaderboardUseCases(IUnitOfWork db, ICurrentActor current, ITime time)
{
    private async Task<Challenge> GetChallengeAsync(long challengeId, CancellationToken ct)
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        var challenge = await db.Set<Challenge>().FirstOrDefaultAsync(c => c.Id == challengeId, ct)
            ?? throw MotivaException.NotFound("Челлендж не найден");
        var stream = await db.Set<CampaignStream>().FirstAsync(s => s.Id == challenge.StreamId, ct);
        var campaign = await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == stream.CampaignId && c.CompanyId == actor.CompanyId, ct)
            ?? throw MotivaException.NotFound("Челлендж не найден");
        return challenge;
    }

    public async Task<LeaderboardView> GetLeaderboardAsync(long challengeId, int limit, CancellationToken ct)
    {
        var challenge = await GetChallengeAsync(challengeId, ct);
        if (limit is < 1 or > 100)
        {
            limit = 50;
        }

        var rows = await db.Set<ChallengeScore>().Where(s => s.ChallengeId == challengeId)
            .OrderByDescending(s => s.Score).ThenBy(s => s.EmployeeId)
            .Take(limit).ToListAsync(ct);
        var employees = await db.Set<Employee>().Where(e => rows.Select(r => r.EmployeeId).Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);
        var top = new List<RatingRowView>();
        long? lastScore = null; int place = 0; int basePlace = 1;
        var i = 0;
        foreach (var row in rows)
        {
            i++;
            if (lastScore != row.Score) { basePlace = i; lastScore = row.Score; }
            place = basePlace;
            var masterId = employees.GetValueOrDefault(row.EmployeeId)?.MasterId ?? 0;
            top.Add(new RatingRowView(masterId, row.Score, place));
        }
        return new LeaderboardView(limit, top, time.UtcNow);
    }

    public async Task<OwnRatingView> GetOwnRatingAsync(long challengeId, CancellationToken ct)
    {
        var challenge = await GetChallengeAsync(challengeId, ct);
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        if (actor.Type != ActorType.User || actor.MasterId is null)
        {
            return new OwnRatingView(false, null, null, null);
        }

        var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.CompanyId == actor.CompanyId && e.MasterId == actor.MasterId, ct);
        if (employee is null)
        {
            return new OwnRatingView(false, null, null, null);
        }

        var me = await db.Set<ChallengeScore>().FirstOrDefaultAsync(s => s.ChallengeId == challengeId && s.EmployeeId == employee.Id, ct);
        if (me is null)
        {
            return new OwnRatingView(false, actor.MasterId, null, null);
        }

        var better = await db.Set<ChallengeScore>().CountAsync(s => s.ChallengeId == challengeId && s.Score > me.Score, ct);
        var ties = await db.Set<ChallengeScore>().CountAsync(s => s.ChallengeId == challengeId && s.Score == me.Score && s.EmployeeId < employee.Id, ct);
        var place = better + ties + 1;
        return new OwnRatingView(true, actor.MasterId, me.Score, place);
    }

    /// <summary>Финализация челленджа (B33): итог неизменяем после final. Читается после ожидания замка.</summary>
    public async Task FinalizeChallengeAsync(long challengeId, CancellationToken ct)
    {
        await db.ExecuteSerializableAsync(async inner =>
        {
            var challenge = await db.Set<Challenge>().FirstOrDefaultAsync(c => c.Id == challengeId, inner);
            if (challenge is null)
            {
                return;
            }

            if (challenge.State == ChallengeState.Final)
            {
                return;
            }

            if (time.UtcNow < challenge.End)
            {
                return;
            }

            var events = await db.Set<ProgressEvent>()
                .Join(db.Set<CampaignTask>(), e => e.TaskId, t => t.Id, (e, t) => new { e, t })
                .Where(x => x.t.StreamId == challenge.StreamId && challenge.Start <= x.e.AcceptedAt && x.e.AcceptedAt < challenge.End)
                .ToListAsync(inner);
            var totals = events.GroupBy(x => x.e.EmployeeId)
                .ToDictionary(g => g.Key, g => (long)g.Sum(x => x.e.CreditedIncrease));

            foreach (var (employeeId, score) in totals)
            {
                var row = await db.Set<ChallengeScore>().FirstOrDefaultAsync(s => s.ChallengeId == challengeId && s.EmployeeId == employeeId, inner)
                    ?? new ChallengeScore { ChallengeId = challengeId, EmployeeId = employeeId, Score = 0 };
                row.Score = score;
                row.FinalizedAt = time.UtcNow;
                if (row.ChallengeId == challengeId)
                {
                    db.Update(row);
                }
                else
                {
                    db.Add(row);
                }
            }
            challenge.State = ChallengeState.Final;
            db.Update(challenge);
            await db.SaveChangesAsync(inner);
        }, ct);
    }
}

public sealed record LeaderboardView(int Limit, List<RatingRowView> Top, DateTimeOffset AsOf);
public sealed record RatingRowView(int MasterId, long Score, int Place);
public sealed record OwnRatingView(bool Present, int? MasterId, long? Score, int? Place);

public sealed class StatementUseCases(IUnitOfWork db, ICurrentActor current, ITime time, IObjectStorage storage, ICache cache)
{
    private Actor Require() => current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");

    public async Task<Statement> CreateAsync(long? employeeId, DateTimeOffset from, DateTimeOffset to, long? resourceId, string? idempotencyKey, CancellationToken ct)
    {
        var actor = Require();
        if (from >= to)
        {
            throw MotivaException.Validation("from раньше to");
        }

        if (resourceId is not null && !await db.Set<Resource>().AnyAsync(r => r.Id == resourceId && r.CompanyId == actor.CompanyId, ct))
        {
            throw MotivaException.Validation("Ресурс не найден");
        }

        StatementScope scope; long? scopeEmployeeId = null;
        if (actor.Type == ActorType.User)
        {
            if (actor.IsAdmin && employeeId is long eid)
            {
                if (!await db.Set<Employee>().AnyAsync(e => e.Id == eid && e.CompanyId == actor.CompanyId, ct))
                {
                    throw MotivaException.Validation("Сотрудник не найден");
                }

                scope = StatementScope.Employee; scopeEmployeeId = eid;
            }
            else if (actor.IsAdmin && employeeId is null)
            {
                scope = StatementScope.Company;
            }
            else
            {
                if (employeeId is not null)
                {
                    throw MotivaException.Forbidden("Только Admin заказывает на другого сотрудника");
                }

                var employee = await db.Set<Employee>().FirstAsync(e => e.CompanyId == actor.CompanyId && e.MasterId == actor.MasterId, ct);
                scope = StatementScope.Employee; scopeEmployeeId = employee.Id;
            }
        }
        else
        {
            throw MotivaException.Forbidden("Выписку заказывает сотрудник или Admin (B35)");
        }

        // транспортная идемпотентность (T05): ключ >=24ч
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            throw MotivaException.Validation("Требуется Idempotency-Key");
        }

        var existing = await db.Set<Statement>().Where(s =>
            s.CompanyId == actor.CompanyId && s.TransportKey == idempotencyKey)
            .OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        var statement = new Statement
        {
            CompanyId = actor.CompanyId,
            OwnerActorType = actor.Type.ToString().ToLower(),
            OwnerActorId = long.Parse(actor.Subject),
            Scope = scope,
            EmployeeId = scopeEmployeeId,
            From = from,
            To = to,
            ResourceId = resourceId,
            TransportKey = idempotencyKey,
        };
        db.Add(statement);
        await db.SaveChangesAsync(ct);
        return statement;
    }

    public async Task<Statement> GetAsync(long id, CancellationToken ct)
    {
        var actor = Require();
        var statement = await db.Set<Statement>().FirstOrDefaultAsync(s => s.Id == id && s.CompanyId == actor.CompanyId, ct)
            ?? throw MotivaException.NotFound("Выписка не найдена");
        if (!CanAccess(actor, statement))
        {
            throw MotivaException.NotFound("Выписка недоступна");
        }

        return statement;
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var actor = Require();
        var statement = await GetAsync(id, ct);
        if (!CanAccess(actor, statement))
        {
            throw MotivaException.NotFound("Выписка недоступна");
        }

        statement.State = StatementState.Deleted;
        foreach (var d in await db.Set<StatementDownload>().Where(d => d.StatementId == id && d.RevokedAt == null).ToListAsync(ct))
        {
            d.RevokedAt = time.UtcNow;
        }

        db.Update(statement);
        await db.SaveChangesAsync(ct);
    }

    public async Task<DownloadLinkView> CreateDownloadLinkAsync(long id, string? idempotencyKey, CancellationToken ct)
    {
        var actor = Require();
        var statement = await GetAsync(id, ct);
        if (statement.State == StatementState.Deleted)
        {
            throw MotivaException.Conflict("Выписка удалена (B37b)");
        }

        if (statement.State != StatementState.Ready || string.IsNullOrEmpty(statement.FileRef))
        {
            throw MotivaException.Conflict("Выписка ещё не готова");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            throw MotivaException.Validation("Требуется Idempotency-Key");
        }

        var existing = await db.Set<StatementDownload>().Where(d => d.StatementId == id && d.TransportKey == idempotencyKey)
            .OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return new DownloadLinkView(existing.Url, existing.ExpiresAt);
        }

        var ttl = TimeSpan.FromSeconds(60);
        var url = await storage.CreatePresignedGetAsync(storage.Bucket(statement.CompanyId), statement.FileRef, ttl, ct);
        var download = new StatementDownload
        {
            StatementId = id,
            Url = url,
            IssuedAt = time.UtcNow,
            ExpiresAt = time.UtcNow.Add(ttl),
            TransportKey = idempotencyKey,
        };
        db.Add(download);
        await db.SaveChangesAsync(ct);
        return new DownloadLinkView(url, download.ExpiresAt);
    }

    public async Task<List<Statement>> ListAsync(CancellationToken ct)
    {
        var actor = Require();
        var q = db.Set<Statement>().Where(s => s.CompanyId == actor.CompanyId);
        if (actor.Type == ActorType.User && !actor.IsAdmin)
        {
            var employee = await db.Set<Employee>().FirstAsync(e => e.CompanyId == actor.CompanyId && e.MasterId == actor.MasterId, ct);
            q = q.Where(s => s.OwnerActorId == employee.Id && s.OwnerActorType == "user");
        }
        return await q.OrderByDescending(s => s.Id).ToListAsync(ct);
    }

    private static bool CanAccess(Actor actor, Statement s) =>
        s.CompanyId == actor.CompanyId &&
        (actor.IsAdmin || (actor.Type == ActorType.User && s.OwnerActorType == "user" && long.TryParse(actor.Subject, out var id) && id == s.OwnerActorId));
}

public sealed record DownloadLinkView(string Url, DateTimeOffset ExpiresAt);

public sealed class AuditUseCases(IUnitOfWork db, ICurrentActor current)
{
    public async Task<List<AuditEntry>> ListAsync(CancellationToken ct)
    {
        var actor = current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");
        if (!actor.IsAdmin)
        {
            throw MotivaException.Forbidden("Аудит — только Admin");
        }

        return await db.Set<AuditEntry>().Where(a => a.CompanyId == actor.CompanyId).OrderByDescending(a => a.Id).ToListAsync(ct);
    }
}
