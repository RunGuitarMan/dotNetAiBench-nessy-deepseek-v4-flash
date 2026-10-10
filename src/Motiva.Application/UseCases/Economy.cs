using System.Text.Json;
using System.Text.Json.Nodes;
using Motiva.Application.Ports;
using Motiva.Domain;

namespace Motiva.Application.UseCases;

/// <summary>
/// Экономические операции: выделение бюджета, событие (S1), списание, ручное начисление,
/// отмены/возвраты. Все выполняются в Serializable-транзакции с бессрочной идемпотентностью (B24/D201).
/// </summary>
public sealed class EconomyUseCases(
    IUnitOfWork db,
    ICurrentActor current,
    ITime time,
    ICompanyCalendar calendar)
{
    private Actor Require() => current.Actor ?? throw MotivaException.Forbidden("Требуется аутентификация");

    // ---------------- Выделение бюджета (B18) ----------------
    public async Task<Operation> AllocateBudgetAsync(long campaignId, long resourceId, int amount, string sourceNo, CancellationToken ct)
    {
        var actor = Require();
        if (!actor.IsAdmin)
        {
            throw MotivaException.Forbidden("Бюджет выделяет только Admin (B18)");
        }

        if (!DomainRules.IsValidPositive(amount))
        {
            throw MotivaException.Validation("Сумма вне диапазона");
        }

        if (!DomainRules.IsValidSourceNo(sourceNo))
        {
            throw MotivaException.Validation("Некорректный номер источника");
        }

        return await db.ExecuteSerializableAsync(async inner =>
        {
            var campaign = await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == campaignId && c.CompanyId == actor.CompanyId, inner)
                ?? throw MotivaException.NotFound("Кампания не найдена");
            if (!await db.Set<CampaignResource>().AnyAsync(cr => cr.CampaignId == campaignId && cr.ResourceId == resourceId, inner))
            {
                throw MotivaException.Validation("Ресурс не входит в кампанию");
            }

            if (await db.Set<Resource>().AnyAsync(r => r.Id == resourceId && r.Status == ResourceStatus.Archived, inner))
            {
                throw MotivaException.Validation("Архивный ресурс нельзя выделять (B07)");
            }

            return await ReplayOrRunAsync(actor, OperationKind.BudgetAllocation, sourceNo,
                () => new AllocBudgetPayload(campaignId, resourceId, amount),
                async () =>
                {
                    var budget = await db.Set<Budget>().FirstOrDefaultAsync(b => b.CampaignId == campaignId && b.ResourceId == resourceId, inner);
                    var isNewBudget = budget is null;
                    budget ??= new Budget { CampaignId = campaignId, ResourceId = resourceId, Remaining = 0 };
                    var newRemaining = budget.Remaining + amount;
                    if (newRemaining > DomainRules.MaxAccumulated)
                    {
                        throw MotivaException.Validation("Бюджет превышает 1e15");
                    }

                    budget.Remaining = newRemaining;
                    if (isNewBudget)
                    {
                        db.Add(budget);
                    }
                    else
                    {
                        db.Update(budget);
                    }

                    var op = MakeOperation(actor, OperationKind.BudgetAllocation, sourceNo, null, amount, resourceId, campaignId, result: OperationResult.Posted, occurred: time.UtcNow);
                    db.Add(op);
                    await db.SaveChangesAsync(inner);
                    db.Add(new OperationLine { OperationId = op.Id, ResourceId = resourceId, Amount = amount });
                    db.Add(new AuditEntry
                    {
                        CompanyId = actor.CompanyId,
                        Actor = actor.InitiatorKey,
                        Target = $"campaign:{campaignId}:resource:{resourceId}",
                        Action = AuditAction.BudgetAllocation,
                        Details = new() { ["amount"] = amount.ToString() },
                        OccurredAt = time.UtcNow,
                    });
                    await db.SaveChangesAsync(inner);
                    return op;
                }, inner);
        }, ct);
    }

    // ---------------- Приём события прогресса (S1/B14–B17) ----------------
    public async Task<EventIngestResult> TransmitProgressAsync(long employeeId, long taskId, int increase, string sourceEventNo, CancellationToken ct)
    {
        var actor = Require();
        if (actor.Type != ActorType.Service)
        {
            throw MotivaException.Forbidden("Прогресс передаёт только сервис (B03h)");
        }

        if (!DomainRules.IsValidPositive(increase))
        {
            throw MotivaException.Validation("Увеличение вне диапазона");
        }

        if (!DomainRules.IsValidSourceNo(sourceEventNo))
        {
            throw MotivaException.Validation("Некорректный номер события");
        }

        return await db.ExecuteSerializableAsync(async inner =>
        {
            var integration = await db.Set<Integration>().FirstOrDefaultAsync(i => i.CompanyId == actor.CompanyId && i.ServiceSub == actor.Subject, inner)
                ?? throw MotivaException.Forbidden("Интеграция не зарегистрирована");

            var task = await db.Set<CampaignTask>().Include(t => t.Rewards).FirstOrDefaultAsync(t => t.Id == taskId, inner)
                ?? throw MotivaException.NotFound("Задание не найдено");
            var stream = await db.Set<CampaignStream>().FirstAsync(s => s.Id == task.StreamId, inner);
            var campaign = await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == stream.CampaignId && c.CompanyId == actor.CompanyId, inner)
                ?? throw MotivaException.NotFound("Кампания не найдена");

            // grant progress по кампании (B04)
            if (!await db.Set<IntegrationGrant>().AnyAsync(g => g.IntegrationId == integration.Id && g.Kind == GrantKind.Progress && g.CampaignId == campaign.Id, inner))
            {
                throw MotivaException.Forbidden("Нет grant progress по кампании");
            }

            var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == actor.CompanyId, inner)
                ?? throw MotivaException.NotFound("Сотрудник не найден");
            if (!employee.IsActive)
            {
                throw MotivaException.Forbidden("Сотрудник заблокирован (B05)");
            }

            // идемпотентность события (B16): повтор возвращает прежний результат
            var existing = await db.Set<ProgressEvent>().FirstOrDefaultAsync(e => e.CompanyId == actor.CompanyId && e.SourceId == integration.Id && e.SourceEventNo == sourceEventNo, inner);
            if (existing is not null)
            {
                if (existing.EmployeeId != employeeId || existing.TaskId != taskId || existing.SentIncrease != increase)
                {
                    throw MotivaException.Conflict("Тот же номер события с другими данными (B16c)");
                }

                return ToResult(existing);
            }

            // сезон и сроки кампании
            var acceptedAt = time.UtcNow;
            if (campaign.State != CampaignState.Published)
            {
                throw MotivaException.Conflict("Кампания не опубликована");
            }

            if (acceptedAt < campaign.Start || acceptedAt >= campaign.End)
            {
                throw MotivaException.Conflict("Вне сроков кампании (B09c)");
            }

            if (campaign.Season != calendar.SeasonOf(acceptedAt))
            {
                throw MotivaException.Conflict("Не текущий сезон");
            }

            if (stream.Status == ResourceStatus.Archived || task.Status == ResourceStatus.Archived)
            {
                throw MotivaException.Conflict("Архивное задание/стрим не принимает события (B15e)");
            }

            // аудитория кампании и задания (B11)
            if (!AudienceRules.IsIncluded(campaign.Audience, employee.Tags) || !AudienceRules.IsIncluded(task.Audience, employee.Tags))
            {
                throw MotivaException.Forbidden("Сотрудник вне аудитории (B11)");
            }

            var periodStart = calendar.PeriodStart(acceptedAt, task.Periodicity);
            var progress = await db.Set<Progress>().FirstOrDefaultAsync(p => p.EmployeeId == employeeId && p.TaskId == taskId && p.PeriodStart == periodStart, inner);
            var isNewProgress = progress is null;
            progress ??= new Progress { EmployeeId = employeeId, TaskId = taskId, PeriodStart = periodStart, Value = 0 };
            var oldValue = progress.Value;
            var credited = Math.Min(task.Goal - oldValue, increase);
            if (credited < 0)
            {
                credited = 0;
            }

            var newValue = Math.Min(task.Goal, oldValue + increase);
            progress.Value = newValue;
            if (isNewProgress)
            {
                db.Add(progress);
            }
            else
            {
                db.Update(progress);
            }

            var completed = newValue >= task.Goal;
            var rewardOp = (Operation?)null;
            var rewardResult = "none";
            string? rewardReason = null;

            if (completed && !await db.Set<Completion>().AnyAsync(c => c.EmployeeId == employeeId && c.TaskId == taskId && c.PeriodStart == periodStart, inner))
            {
                db.Add(new Completion { EmployeeId = employeeId, TaskId = taskId, PeriodStart = periodStart });
                // очки стрима (B29a)
                if (task.StreamPoints > 0)
                {
                    var score = await db.Set<StreamScore>().FirstOrDefaultAsync(s => s.EmployeeId == employeeId && s.StreamId == stream.Id && s.Season == campaign.Season, inner);
                    var isNewScore = score is null;
                    score ??= new StreamScore { EmployeeId = employeeId, StreamId = stream.Id, Season = campaign.Season, Points = 0 };
                    score.Points += task.StreamPoints;
                    if (score.Points > DomainRules.MaxAccumulated)
                    {
                        throw MotivaException.Validation("Очки превышают 1e15");
                    }

                    if (isNewScore)
                    {
                        db.Add(score);
                    }
                    else
                    {
                        db.Update(score);
                    }
                }
                // вехи → достижения (B29/B30)
                await GrantMilestonesAsync(employeeId, stream.Id, campaign.Season, inner);
                // награда (B20)
                if (task.Rewards.Count > 0)
                {
                    (rewardOp, rewardResult, rewardReason) = await GrantRewardAsync(employee, task, stream, campaign, inner);
                }
                else
                {
                    rewardResult = "none";
                }
            }

            var ev = new ProgressEvent
            {
                CompanyId = actor.CompanyId,
                SourceId = integration.Id,
                SourceEventNo = sourceEventNo,
                EmployeeId = employeeId,
                TaskId = taskId,
                SentIncrease = increase,
                CreditedIncrease = credited,
                AcceptedAt = acceptedAt,
                Completed = completed,
                RewardOperationId = rewardOp?.Id,
                RewardResult = rewardResult,
                RewardReason = rewardReason,
            };
            db.Add(ev);

            // рейтинг челленджа (B31): credited каждого нового события
            await UpdateChallengeScoresAsync(employeeId, stream.Id, acceptedAt, credited, inner);

            await db.SaveChangesAsync(inner);
            return new EventIngestResult(ev.Id, true, credited, completed, rewardResult, rewardReason, acceptedAt);
        }, ct);
    }

    private async Task GrantMilestonesAsync(long employeeId, long streamId, int season, CancellationToken ct)
    {
        var score = await db.Set<StreamScore>().FirstOrDefaultAsync(s => s.EmployeeId == employeeId && s.StreamId == streamId && s.Season == season, ct);
        if (score is null)
        {
            return;
        }

        var milestones = await db.Set<Milestone>().Where(m => m.StreamId == streamId && m.Threshold <= score.Points).ToListAsync(ct);
        foreach (var m in milestones)
        {
            if (!await db.Set<AchievementGrant>().AnyAsync(a => a.EmployeeId == employeeId && a.AchievementId == m.AchievementId && a.Season == season, ct))
            {
                db.Add(new AchievementGrant { EmployeeId = employeeId, AchievementId = m.AchievementId, Season = season, GrantedAt = time.UtcNow });
            }
        }
    }

    /// <summary>Пакетная награда all-or-nothing (B20). Возвращает (operation, result, reason).</summary>
    private async Task<(Operation? op, string result, string? reason)> GrantRewardAsync(
        Employee employee, CampaignTask task, CampaignStream stream, Campaign campaign, CancellationToken ct)
    {
        // все позиции целиком либо ни одна; недоступный ресурс → declined «ресурс недоступен» (B07e)
        var budgets = new List<Budget>();
        foreach (var r in task.Rewards)
        {
            var resource = await db.Set<Resource>().FirstAsync(x => x.Id == r.ResourceId, ct);
            if (resource.Status == ResourceStatus.Archived)
            {
                return (null, "declined", "resource_unavailable");
            }

            var budget = await db.Set<Budget>().FirstOrDefaultAsync(b => b.CampaignId == campaign.Id && b.ResourceId == r.ResourceId, ct);
            if (budget is null || budget.Remaining < r.Amount)
            {
                return (null, "declined", "insufficient_funds");
            }

            budgets.Add(budget);
        }

        var op = MakeOperation(Require(), OperationKind.TaskReward, "event:" + Guid.NewGuid().ToString("N")[..8],
            task.Id, task.Rewards[0].Amount, task.Rewards[0].ResourceId, campaign.Id,
            result: OperationResult.Posted, occurred: time.UtcNow);
        db.Add(op);
        await db.SaveChangesAsync(ct);

        var wallet = await db.Set<Wallet>().FirstAsync(w => w.EmployeeId == employee.Id, ct);
        foreach (var r in task.Rewards)
        {
            db.Add(new OperationLine { OperationId = op.Id, ResourceId = r.ResourceId, Amount = r.Amount });
            var budget = budgets.First(b => b.ResourceId == r.ResourceId);
            budget.Remaining -= r.Amount;
            db.Update(budget);
            await AddWalletMovementAsync(wallet.Id, op.Id, r.ResourceId, r.Amount, campaign.Id, null, OperationKind.TaskReward, ct);
        }
        await db.SaveChangesAsync(ct);
        return (op, "granted", null);
    }

    private async Task AddWalletMovementAsync(long walletId, long operationId, long resourceId, long delta, long? campaignId, long? originalOperationId, OperationKind kind, CancellationToken ct)
    {
        var balance = await db.Set<WalletBalance>().FirstOrDefaultAsync(b => b.WalletId == walletId && b.ResourceId == resourceId, ct);
        var isNewBalance = balance is null;
        balance ??= new WalletBalance { WalletId = walletId, ResourceId = resourceId, Balance = 0 };
        var newBalance = balance.Balance + delta;
        if (newBalance < 0)
        {
            throw MotivaException.Conflict("Баланс не может быть отрицательным");
        }

        if (newBalance > DomainRules.MaxAccumulated)
        {
            throw MotivaException.Validation("Баланс превышает 1e15");
        }

        balance.Balance = newBalance;
        if (isNewBalance)
        {
            db.Add(balance);
        }
        else
        {
            db.Update(balance);
        }

        db.Add(new WalletMovement
        {
            WalletId = walletId,
            OperationId = operationId,
            ResourceId = resourceId,
            Delta = delta,
            BalanceAfter = newBalance,
            OccurredAt = time.UtcNow,
            Kind = kind,
            CampaignId = campaignId,
            OriginalOperationId = originalOperationId,
        });
    }

    private async Task UpdateChallengeScoresAsync(long employeeId, long streamId, DateTimeOffset acceptedAt, int credited, CancellationToken ct)
    {
        var challenges = await db.Set<Challenge>().Where(c => c.StreamId == streamId && c.State == ChallengeState.Open && c.Start <= acceptedAt && acceptedAt < c.End).ToListAsync(ct);
        foreach (var ch in challenges)
        {
            var score = await db.Set<ChallengeScore>().FirstOrDefaultAsync(s => s.ChallengeId == ch.Id && s.EmployeeId == employeeId, ct);
            var isNewScore = score is null;
            score ??= new ChallengeScore { ChallengeId = ch.Id, EmployeeId = employeeId, Score = 0 };
            score.Score += credited;
            if (score.Score > DomainRules.MaxAccumulated)
            {
                throw MotivaException.Validation("Счёт превышает 1e15");
            }

            if (isNewScore)
            {
                db.Add(score);
            }
            else
            {
                db.Update(score);
            }
        }
    }

    private static EventIngestResult ToResult(ProgressEvent e) =>
        new(e.Id, true, e.CreditedIncrease, e.Completed, e.RewardResult ?? "none", e.RewardReason, e.AcceptedAt);

    // ---------------- Списание (B23) ----------------
    public async Task<Operation> CreateSpendAsync(long employeeId, long resourceId, int amount, long purchaseSystemId, string basis, string sourceNo, CancellationToken ct)
    {
        var actor = Require();
        return await ExecuteSpendAsync(actor, employeeId, resourceId, amount, purchaseSystemId, basis, sourceNo, requireOwnSpend: true, ct);
    }

    public async Task<Operation> IntegrationSpendAsync(long employeeId, long resourceId, int amount, long purchaseSystemId, string basis, string sourceNo, CancellationToken ct)
    {
        var actor = Require();
        if (actor.Type != ActorType.Service)
        {
            throw MotivaException.Forbidden("Списание интеграцией — только сервис");
        }

        return await ExecuteSpendAsync(actor, employeeId, resourceId, amount, purchaseSystemId, basis, sourceNo, requireOwnSpend: false, ct);
    }

    private async Task<Operation> ExecuteSpendAsync(Actor actor, long employeeId, long resourceId, int amount, long purchaseSystemId, string basis, string sourceNo, bool requireOwnSpend, CancellationToken ct)
    {
        if (!DomainRules.IsValidPositive(amount))
        {
            throw MotivaException.Validation("Сумма вне диапазона");
        }

        if (string.IsNullOrWhiteSpace(basis) || basis.Length > 2000)
        {
            throw MotivaException.Validation("Некорректное основание");
        }

        if (!DomainRules.IsValidSourceNo(sourceNo))
        {
            throw MotivaException.Validation("Некорректный номер");
        }

        return await db.ExecuteSerializableAsync(async inner =>
        {
            var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == actor.CompanyId, inner)
                ?? throw MotivaException.NotFound("Сотрудник не найден");
            if (!employee.IsActive)
            {
                throw MotivaException.Forbidden("Получатель заблокирован (B23c)");
            }

            if (requireOwnSpend && actor.Type == ActorType.User)
            {
                if (actor.MasterId != employee.MasterId)
                {
                    throw MotivaException.Forbidden("Только своя трата");
                }
            }

            var system = await db.Set<PurchaseSystem>().FirstOrDefaultAsync(p => p.Id == purchaseSystemId && p.CompanyId == actor.CompanyId, inner)
                ?? throw MotivaException.NotFound("Система покупок не найдена");
            if (requireOwnSpend && actor.Type == ActorType.User)
            {
                // сотрудник может тратить через любую зарегистрированную систему (B23a)
            }
            else
            {
                var integration = await db.Set<Integration>().FirstOrDefaultAsync(i => i.CompanyId == actor.CompanyId && i.ServiceSub == actor.Subject, inner)
                    ?? throw MotivaException.Forbidden("Интеграция не зарегистрирована");
                if (!system.ResourceIds.Contains(resourceId))
                {
                    throw MotivaException.Validation("Система не принимает этот ресурс (B23d)");
                }

                if (!await db.Set<IntegrationGrant>().AnyAsync(g => g.IntegrationId == integration.Id && g.Kind == GrantKind.Spend && g.ResourceId == resourceId && g.PurchaseSystemId == purchaseSystemId, inner))
                {
                    throw MotivaException.Forbidden("Нет grant spend (B04)");
                }
            }
            if (await db.Set<Resource>().AnyAsync(r => r.Id == resourceId && r.Status == ResourceStatus.Archived, inner))
            {
                throw MotivaException.ResourceUnavailable("Архивный ресурс нельзя тратить (B07)");
            }

            return await ReplayOrRunAsync(actor, OperationKind.Spend, sourceNo,
                () => new SpendPayload(employeeId, resourceId, amount, purchaseSystemId, basis),
                async () =>
                {
                    var wallet = await db.Set<Wallet>().FirstAsync(w => w.EmployeeId == employeeId, inner);
                    var balance = await db.Set<WalletBalance>().FirstOrDefaultAsync(b => b.WalletId == wallet.Id && b.ResourceId == resourceId, inner);
                    if (balance is null || balance.Balance < amount)
                    {
                        // экономический отказ (B23e): сохранённый declined
                        var declined = MakeOperation(actor, OperationKind.Spend, sourceNo, employeeId, amount, resourceId, null,
                            result: OperationResult.Declined, occurred: time.UtcNow, rejectReason: "insufficient_funds", purchaseSystemId: purchaseSystemId);
                        declined.Basis = basis;
                        db.Add(declined);
                        await db.SaveChangesAsync(inner);
                        db.Add(new OperationLine { OperationId = declined.Id, ResourceId = resourceId, Amount = amount });
                        await db.SaveChangesAsync(inner);
                        return declined;
                    }
                    var op = MakeOperation(actor, OperationKind.Spend, sourceNo, employeeId, amount, resourceId, null,
                        result: OperationResult.Posted, occurred: time.UtcNow, purchaseSystemId: purchaseSystemId);
                    op.Basis = basis;
                    db.Add(op);
                    await db.SaveChangesAsync(inner);
                    db.Add(new OperationLine { OperationId = op.Id, ResourceId = resourceId, Amount = amount });
                    await AddWalletMovementAsync(wallet.Id, op.Id, resourceId, -amount, null, null, OperationKind.Spend, inner);
                    await db.SaveChangesAsync(inner);
                    return op;
                }, inner);
        }, ct);
    }

    // ---------------- Ручное начисление (B21) ----------------
    public async Task<Operation> ManualAccrualAsync(long campaignId, long employeeId, long resourceId, int amount, string basis, string sourceNo, CancellationToken ct)
    {
        var actor = Require();
        return await ExecuteManualAccrualAsync(actor, campaignId, employeeId, resourceId, amount, basis, sourceNo, ct);
    }

    public async Task<Operation> IntegrationManualAccrualAsync(long campaignId, long employeeId, long resourceId, int amount, string basis, string sourceNo, CancellationToken ct)
    {
        var actor = Require();
        if (actor.Type != ActorType.Service)
        {
            throw MotivaException.Forbidden("Только сервис");
        }

        return await ExecuteManualAccrualAsync(actor, campaignId, employeeId, resourceId, amount, basis, sourceNo, ct);
    }

    private async Task<Operation> ExecuteManualAccrualAsync(Actor actor, long campaignId, long employeeId, long resourceId, int amount, string basis, string sourceNo, CancellationToken ct)
    {
        if (!DomainRules.IsValidPositive(amount))
        {
            throw MotivaException.Validation("Сумма вне диапазона");
        }

        if (string.IsNullOrWhiteSpace(basis) || basis.Length > 2000)
        {
            throw MotivaException.Validation("Некорректное основание");
        }

        if (!DomainRules.IsValidSourceNo(sourceNo))
        {
            throw MotivaException.Validation("Некорректный номер");
        }

        return await db.ExecuteSerializableAsync(async inner =>
        {
            var campaign = await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == campaignId && c.CompanyId == actor.CompanyId, inner)
                ?? throw MotivaException.NotFound("Кампания не найдена");
            if (campaign.State != CampaignState.Published)
            {
                throw MotivaException.Conflict("Кампания не действует");
            }

            var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == actor.CompanyId, inner)
                ?? throw MotivaException.NotFound("Сотрудник не найден");
            if (!employee.IsActive)
            {
                throw MotivaException.Forbidden("Получатель заблокирован (B21a)");
            }

            if (actor.Type == ActorType.Service)
            {
                var integration = await db.Set<Integration>().FirstOrDefaultAsync(i => i.CompanyId == actor.CompanyId && i.ServiceSub == actor.Subject, inner)
                    ?? throw MotivaException.Forbidden("Интеграция не зарегистрирована");
                if (!await db.Set<IntegrationGrant>().AnyAsync(g => g.IntegrationId == integration.Id && g.Kind == GrantKind.Accrual && g.CampaignId == campaignId && g.ResourceId == resourceId, inner))
                {
                    throw MotivaException.Forbidden("Нет grant accrual (B04)");
                }
            }
            else if (!actor.IsAdmin)
            {
                // владелец кампании
                var owner = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == campaign.OwnerEmployeeId && e.CompanyId == actor.CompanyId, inner);
                if (owner is null || owner.MasterId != actor.MasterId)
                {
                    throw MotivaException.Forbidden("Только владелец кампании или Admin (B21)");
                }
            }
            if (await db.Set<Resource>().AnyAsync(r => r.Id == resourceId && r.Status == ResourceStatus.Archived, inner))
            {
                throw MotivaException.ResourceUnavailable("Архивный ресурс (B07)");
            }

            return await ReplayOrRunAsync(actor, OperationKind.ManualAccrual, sourceNo,
                () => new ManualAccrualPayload(campaignId, employeeId, resourceId, amount, basis),
                async () =>
                {
                    var budget = await db.Set<Budget>().FirstOrDefaultAsync(b => b.CampaignId == campaignId && b.ResourceId == resourceId, inner);
                    if (budget is null || budget.Remaining < amount)
                    {
                        var declined = MakeOperation(actor, OperationKind.ManualAccrual, sourceNo, employeeId, amount, resourceId, campaignId,
                            OperationResult.Declined, time.UtcNow, "insufficient_funds");
                        declined.Basis = basis;
                        db.Add(declined);
                        await db.SaveChangesAsync(inner);
                        db.Add(new OperationLine { OperationId = declined.Id, ResourceId = resourceId, Amount = amount });
                        await db.SaveChangesAsync(inner);
                        return declined;
                    }
                    budget.Remaining -= amount;
                    db.Update(budget);
                    var op = MakeOperation(actor, OperationKind.ManualAccrual, sourceNo, employeeId, amount, resourceId, campaignId,
                        OperationResult.Posted, time.UtcNow);
                    op.Basis = basis;
                    db.Add(op);
                    await db.SaveChangesAsync(inner);
                    db.Add(new OperationLine { OperationId = op.Id, ResourceId = resourceId, Amount = amount });
                    var wallet = await db.Set<Wallet>().FirstAsync(w => w.EmployeeId == employeeId, inner);
                    await AddWalletMovementAsync(wallet.Id, op.Id, resourceId, amount, campaignId, null, OperationKind.ManualAccrual, inner);
                    await db.SaveChangesAsync(inner);
                    return op;
                }, inner);
        }, ct);
    }

    // ---------------- Отмена начисления (B26/D202) ----------------
    public async Task<Operation> CancelAccrualAsync(long originalOperationId, string basis, string sourceNo, CancellationToken ct)
    {
        var actor = Require();
        return await ExecuteCancellationAsync(actor, originalOperationId, basis, sourceNo, OperationKind.AccrualCancellation, ct);
    }

    // ---------------- Возврат списания (B27) ----------------
    public async Task<Operation> RefundSpendAsync(long originalOperationId, string basis, string sourceNo, CancellationToken ct)
    {
        var actor = Require();
        return await ExecuteCancellationAsync(actor, originalOperationId, basis, sourceNo, OperationKind.SpendRefund, ct);
    }

    private async Task<Operation> ExecuteCancellationAsync(Actor actor, long originalOperationId, string basis, string sourceNo, OperationKind kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(basis) || basis.Length > 2000)
        {
            throw MotivaException.Validation("Некорректное основание");
        }

        if (!DomainRules.IsValidSourceNo(sourceNo))
        {
            throw MotivaException.Validation("Некорректный номер");
        }

        return await db.ExecuteSerializableAsync(async inner =>
        {
            var original = await db.Set<Operation>().FirstOrDefaultAsync(o => o.Id == originalOperationId && o.CompanyId == actor.CompanyId, inner)
                ?? throw MotivaException.NotFound("Исходная операция не найдена");
            var expectedKind = kind == OperationKind.AccrualCancellation ? OperationKind.ManualAccrual : OperationKind.Spend;
            if (original.Kind != expectedKind && !(kind == OperationKind.AccrualCancellation && original.Kind == OperationKind.TaskReward))
            {
                throw MotivaException.Validation($"Отмена {kind} не применима к операции {original.Kind}");
            }

            // полномочия (B26/B27/RA4)
            await AssertCancellationRightsAsync(actor, original, kind, inner);

            return await ReplayOrRunAsync(actor, kind, sourceNo,
                () => new CancellationPayload(originalOperationId, basis),
                async () =>
                {
                    if (original.CancellationState == CancellationState.Posted)
                    {
                        throw MotivaException.Conflict("Исходная операция уже успешно отменена (B25d)");
                    }

                    var lines = await db.Set<OperationLine>().Where(l => l.OperationId == original.Id).ToListAsync(inner);
                    if (lines.Count == 0)
                    {
                        throw MotivaException.Validation("Операция не имеет позиций");
                    }

                    var employeeId = original.EmployeeId
                        ?? throw MotivaException.Validation("Операция не относится к сотруднику");
                    var wallet = await db.Set<Wallet>().FirstAsync(w => w.EmployeeId == employeeId, inner);

                    // достаточность всех балансов (B26c)
                    var balances = new Dictionary<long, WalletBalance>();
                    foreach (var line in lines)
                    {
                        var b = await db.Set<WalletBalance>().FirstOrDefaultAsync(x => x.WalletId == wallet.Id && x.ResourceId == line.ResourceId, inner);
                        if (b is null || b.Balance < line.Amount)
                        {
                            var declined = MakeOperation(actor, kind, sourceNo, employeeId, lines[0].Amount, lines[0].ResourceId, original.CampaignId,
                                OperationResult.Declined, time.UtcNow, "insufficient_funds", originalOperationId: original.Id);
                            declined.Basis = basis;
                            db.Add(declined);
                            await db.SaveChangesAsync(inner);
                            foreach (var l in lines)
                            {
                                db.Add(new OperationLine { OperationId = declined.Id, ResourceId = l.ResourceId, Amount = l.Amount });
                            }

                            await db.SaveChangesAsync(inner);
                            return declined;
                        }
                        balances[line.ResourceId] = b;
                    }

                    var op = MakeOperation(actor, kind, sourceNo, employeeId, lines[0].Amount, lines[0].ResourceId, original.CampaignId,
                        OperationResult.Posted, time.UtcNow, originalOperationId: original.Id);
                    op.Basis = basis;
                    db.Add(op);
                    await db.SaveChangesAsync(inner);
                    foreach (var l in lines)
                    {
                        db.Add(new OperationLine { OperationId = op.Id, ResourceId = l.ResourceId, Amount = l.Amount });
                    }

                    foreach (var line in lines)
                    {
                        var balance = balances[line.ResourceId];
                        if (kind == OperationKind.AccrualCancellation)
                        {
                            // возврат в исходный бюджет (B26b) и из кошелька
                            var budget = await db.Set<Budget>().FirstOrDefaultAsync(b => b.CampaignId == original.CampaignId && b.ResourceId == line.ResourceId, inner);
                            var isNewBudget = budget is null;
                            budget ??= new Budget { CampaignId = original.CampaignId!.Value, ResourceId = line.ResourceId, Remaining = 0 };
                            var newRemaining = budget.Remaining + line.Amount;
                            if (newRemaining > DomainRules.MaxAccumulated)
                            {
                                throw MotivaException.Validation("Бюджет превышает 1e15");
                            }

                            budget.Remaining = newRemaining;
                            if (isNewBudget)
                            {
                                db.Add(budget);
                            }
                            else
                            {
                                db.Update(budget);
                            }
                        }
                        // отмена начисления списывает с кошелька (возврат в бюджет); возврат списания — начисляет обратно
                        var movementDelta = kind == OperationKind.SpendRefund ? line.Amount : -line.Amount;
                        await AddWalletMovementAsync(wallet.Id, op.Id, line.ResourceId, movementDelta, original.CampaignId, original.Id, kind, inner);
                    }
                    original.CancellationState = CancellationState.Posted;
                    db.Update(original);
                    db.Add(new AuditEntry
                    {
                        CompanyId = actor.CompanyId,
                        Actor = actor.InitiatorKey,
                        Target = $"operation:{original.Id}",
                        Action = AuditAction.Correction,
                        Details = new() { ["kind"] = kind.ToString() },
                        OccurredAt = time.UtcNow,
                    });
                    await db.SaveChangesAsync(inner);
                    return op;
                }, inner);
        }, ct);
    }

    private async Task AssertCancellationRightsAsync(Actor actor, Operation original, OperationKind kind, CancellationToken ct)
    {
        if (actor.IsAdmin)
        {
            return;
        }

        var employee = original.EmployeeId is long eid
            ? await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == eid, ct)
            : null;
        var recipientBlocked = employee is null || !employee.IsActive;

        if (kind == OperationKind.AccrualCancellation)
        {
            // владелец исходной кампании (активный получатель); заблокированный — только Admin (RA4)
            if (recipientBlocked)
            {
                throw MotivaException.Forbidden("Отмена заблокированного — только Admin (RA4)");
            }

            var campaign = original.CampaignId is long cid
                ? await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == cid, ct)
                : null;
            var owner = campaign is not null
                ? await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == campaign.OwnerEmployeeId, ct)
                : null;
            if (owner is null || owner.MasterId != actor.MasterId)
            {
                throw MotivaException.Forbidden("Отменяет владелец кампании или Admin (B26)");
            }
        }
        else
        {
            // возврат списания: та же система покупок + актуальный grant (B27)
            if (actor.Type != ActorType.Service)
            {
                throw MotivaException.Forbidden("Возврат списания — Admin или та же система (B27)");
            }

            var integration = await db.Set<Integration>().FirstOrDefaultAsync(i => i.CompanyId == actor.CompanyId && i.ServiceSub == actor.Subject, ct)
                ?? throw MotivaException.Forbidden("Интеграция не зарегистрирована");
            var lines = await db.Set<OperationLine>().Where(l => l.OperationId == original.Id).ToListAsync(ct);
            foreach (var line in lines)
            {
                if (!await db.Set<IntegrationGrant>().AnyAsync(g => g.IntegrationId == integration.Id && g.Kind == GrantKind.Spend
                    && g.ResourceId == line.ResourceId && g.PurchaseSystemId == original.PurchaseSystemId, ct))
                {
                    throw MotivaException.Forbidden("Нет актуального grant spend (B27)");
                }
            }
        }
    }

    // ---------------- Общий механизм бессрочной идемпотентности (B24/D201) ----------------
    private async Task<Operation> ReplayOrRunAsync(Actor actor, OperationKind kind, string sourceNo,
        Func<object> payloadFactory, Func<Task<Operation>> run, CancellationToken ct)
    {
        var canonical = CanonicalJson(payloadFactory());
        var request = await db.Set<OperationRequest>().FirstOrDefaultAsync(r =>
            r.CompanyId == actor.CompanyId && r.InitiatorKey == actor.InitiatorKey && r.Kind == kind && r.SourceNo == sourceNo, ct);

        if (request is not null)
        {
            if (request.CanonicalPayload != canonical)
            {
                throw MotivaException.Conflict("Изменённые существенные данные с прежним номером (B24c)");
            }

            var op = await db.Set<Operation>().FirstAsync(o => o.Id == request.OperationId, ct);
            return op; // прежний результат (B24b)
        }

        var result = await run();

        db.Add(new OperationRequest
        {
            CompanyId = actor.CompanyId,
            InitiatorKey = actor.InitiatorKey,
            Kind = kind,
            SourceNo = sourceNo,
            CanonicalPayload = canonical,
            Result = result.Result,
            HttpStatus = result.Result == OperationResult.Posted ? 201 : 201,
            ResponseView = JsonSerializer.Serialize(new { result.Id, result.Result, result.RejectReason }),
            OperationId = result.Id,
        });
        await db.SaveChangesAsync(ct);
        return result;
    }

    private static string CanonicalJson(object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload);
        return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "{}";
    }

    private static Operation MakeOperation(Actor actor, OperationKind kind, string sourceNo, long? employeeId,
        int amount, long resourceId, long? campaignId, OperationResult result, DateTimeOffset occurred,
        string? rejectReason = null, long? originalOperationId = null, long? purchaseSystemId = null)
    {
        return new Operation
        {
            CompanyId = actor.CompanyId,
            InitiatorKey = actor.InitiatorKey,
            Kind = kind,
            SourceNo = sourceNo,
            EmployeeId = employeeId,
            Result = result,
            OccurredAt = occurred,
            CampaignId = campaignId,
            PurchaseSystemId = purchaseSystemId,
            OriginalOperationId = originalOperationId,
            RejectReason = rejectReason,
        };
    }

    private record AllocBudgetPayload(long campaignId, long resourceId, int amount);
    private record SpendPayload(long employeeId, long resourceId, int amount, long purchaseSystemId, string basis);
    private record ManualAccrualPayload(long campaignId, long employeeId, long resourceId, int amount, string basis);
    private record CancellationPayload(long originalOperationId, string basis);
}

/// <summary>Результат приёма события (OpenAPI EventResult).</summary>
public sealed record EventIngestResult(long EventId, bool Accepted, int CreditedIncrease, bool Completed, string RewardResult, string? RewardReason, DateTimeOffset AcceptedAt);
