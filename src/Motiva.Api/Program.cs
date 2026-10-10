using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Motiva.Api;
using Motiva.Application;
using Motiva.Application.Ports;
using Motiva.Application.UseCases;
using Motiva.Domain;
using Motiva.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.AddMotivaInfrastructure(cfg);

// Use cases
builder.Services.AddScoped<BootstrapUseCases>();
builder.Services.AddScoped<EmployeeUseCases>();
builder.Services.AddScoped<ResourceUseCases>();
builder.Services.AddScoped<IntegrationUseCases>();
builder.Services.AddScoped<PurchaseSystemUseCases>();
builder.Services.AddScoped<AchievementUseCases>();
builder.Services.AddScoped<CampaignUseCases>();
builder.Services.AddScoped<StreamTaskUseCases>();
builder.Services.AddScoped<MilestoneChallengeUseCases>();
builder.Services.AddScoped<EconomyUseCases>();
builder.Services.AddScoped<WalletReadUseCases>();
builder.Services.AddScoped<MeReadUseCases>();
builder.Services.AddScoped<LeaderboardUseCases>();
builder.Services.AddScoped<StatementUseCases>();
builder.Services.AddScoped<AuditUseCases>();

builder.Services.AddScoped<ICurrentActor, HttpCurrentActor>();

builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

// ---- JWT (03_AUTH.md / T03) ----
var issuer = cfg["Auth:Issuer"] ?? "motiva-benchmark";
var audience = cfg["Auth:Audience"] ?? "motiva-api";
var signingKeyBase64 = cfg["Auth:SigningKeyBase64"]
    ?? throw new InvalidOperationException("Auth:SigningKeyBase64 not configured");
var keyBytes = Convert.FromBase64String(signingKeyBase64); // подпись по Base64-байтам ключа

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            RoleClaimType = "role",
            NameClaimType = "sub",
        };
        o.MapInboundClaims = false;
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.Use(async (ctx, next) =>
{
    var user = ctx.User;
    if (user.Identity?.IsAuthenticated == true)
    {
        var actor = ActorFrom(user);
        if (actor is not null)
        {
            ctx.Items["actor"] = actor;
        }
    }
    await next();
});

app.UseMiddleware<MotivaErrorHandling>();
app.UseAuthorization();

var v1 = app.MapGroup("/api/v1");
var internal_ = app.MapGroup("/internal/v1");

// ---------- Bootstrap (доверенный вход, T03) ----------
internal_.MapPost("/bootstrap", async (BootstrapRequest req, BootstrapUseCases uc, CancellationToken ct) =>
{
    var company = await uc.CreateCompanyAsync(req.CompanyName, req.AdminMasterId, ct);
    return Results.Created("/internal/v1/bootstrap", new BootstrapResponse(company.Id, 0));
});

// ---------- Сотрудники (admin) ----------
v1.MapGet("/employees", async (EmployeeUseCases uc, CancellationToken ct) =>
{
    var list = await uc.ListEmployeesAsync(ct);
    return Results.Ok(Page(list.Select(EmpDto).ToList()));
});
v1.MapPost("/employees", async (EmployeeCreateDto dto, EmployeeUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var emp = await uc.CreateEmployeeAsync(dto.MasterId, dto.Tags ?? new(), ct);
    return Results.Created($"/api/v1/employees/{emp.Id}", EmpDto(emp));
});
v1.MapGet("/employees/{employeeId:long}", async (long employeeId, EmployeeUseCases uc, CancellationToken ct) =>
{
    var emp = await uc.GetEmployeeAsync(employeeId, ct);
    return Results.Ok(EmpDto(emp)).WithEtag(emp.Etag());
});
v1.MapPatch("/employees/{employeeId:long}", async (long employeeId, EmployeePatchDto dto, EmployeeUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var emp = await uc.UpdateEmployeeAsync(employeeId, dto.Tags, dto.IsActive, IfMatch(http), ct);
    return Results.Ok(EmpDto(emp));
});

// ---------- Ресурсы (admin) ----------
v1.MapGet("/resources", async (ResourceUseCases uc, CancellationToken ct) =>
    Results.Ok(Page((await uc.ListResourcesAsync(ct)).Select(ResDto).ToList())));
v1.MapPost("/resources", async (ResourceCreateDto dto, ResourceUseCases uc, CancellationToken ct) =>
    Results.Created($"/api/v1/resources/{0}", ResDto(await uc.CreateResourceAsync(dto.Code, dto.Name, ct))));
v1.MapGet("/resources/{resourceId:long}", async (long resourceId, ResourceUseCases uc, CancellationToken ct) =>
{
    var r = await uc.GetResourceAsync(resourceId, ct);
    return Results.Ok(ResDto(r)).WithEtag(r.Etag());
});
v1.MapPatch("/resources/{resourceId:long}", async (long resourceId, ResourcePatchDto dto, ResourceUseCases uc, HttpContext http, CancellationToken ct) =>
    Results.Ok(ResDto(await uc.UpdateResourceAsync(resourceId, dto.Name, ParseStatus(dto.Status), IfMatch(http), ct))));

// ---------- Интеграции / grants ----------
v1.MapGet("/integrations", async (IntegrationUseCases uc, CancellationToken ct) =>
    Results.Ok(Page((await uc.ListIntegrationsAsync(ct)).Select(i => new IntegrationDto(i.Id, i.Name, i.ServiceSub, i.CompanyId)).ToList())));
v1.MapPost("/integrations", async (IntegrationCreateDto dto, IntegrationUseCases uc, CancellationToken ct) =>
{
    var i = await uc.CreateIntegrationAsync(dto.Name, dto.ServiceSub, ct);
    return Results.Created($"/api/v1/integrations/{i.Id}", new IntegrationDto(i.Id, i.Name, i.ServiceSub, i.CompanyId));
});
v1.MapGet("/integrations/{integrationId:long}/grants", async (long integrationId, IntegrationUseCases uc, CancellationToken ct) =>
{
    var etag = await uc.GrantsEtagAsync(integrationId, ct);
    var grants = await uc.ListGrantsAsync(integrationId, ct);
    return Results.Ok(Page(grants.Select(ToGrantDto).ToList()))
        .WithEtag(etag);
});
v1.MapPut("/integrations/{integrationId:long}/grants", async (long integrationId, GrantListDto dto, IntegrationUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var (grants, etag) = await uc.ReplaceGrantsAsync(integrationId, dto.Grants.Select(g => new IntegrationGrant
    {
        Kind = ParseGrant(g.Kind),
        CampaignId = g.CampaignId,
        ResourceId = g.ResourceId,
        PurchaseSystemId = g.PurchaseSystemId,
    }).ToList(), IfMatch(http), ct);
    return Results.Ok(Page(grants.Select(ToGrantDto).ToList())).WithEtag(etag);
});

// ---------- Системы покупок ----------
v1.MapGet("/purchase-systems", async (PurchaseSystemUseCases uc, CancellationToken ct) =>
    Results.Ok(Page((await uc.ListAsync(ct)).Select(PsDto).ToList())));
v1.MapPost("/purchase-systems", async (PurchaseSystemCreateDto dto, PurchaseSystemUseCases uc, CancellationToken ct) =>
    Results.Created($"/api/v1/purchase-systems/{0}", PsDto(await uc.CreateAsync(dto.Name, ct))));
v1.MapGet("/purchase-systems/{id:long}", async (long id, PurchaseSystemUseCases uc, CancellationToken ct) =>
{
    var p = await uc.GetAsync(id, ct);
    return Results.Ok(PsDto(p)).WithEtag(p.Etag());
});
v1.MapPatch("/purchase-systems/{id:long}", async (long id, PurchaseSystemPatchDto dto, PurchaseSystemUseCases uc, HttpContext http, CancellationToken ct) =>
    Results.Ok(PsDto(await uc.UpdateNameAsync(id, dto.Name!, IfMatch(http), ct))));
v1.MapPut("/purchase-systems/{id:long}/resources", async (long id, List<long> resourceIds, PurchaseSystemUseCases uc, HttpContext http, CancellationToken ct) =>
    Results.Ok(await uc.SetResourcesAsync(id, resourceIds, IfMatch(http), ct)));

// ---------- Достижения ----------
v1.MapGet("/achievements", async (AchievementUseCases uc, CancellationToken ct) =>
    Results.Ok(Page((await uc.ListAsync(ct)).Select(AchDto).ToList())));
v1.MapPost("/achievements", async (AchievementCreateDto dto, AchievementUseCases uc, CancellationToken ct) =>
    Results.Created($"/api/v1/achievements/{0}", AchDto(await uc.CreateAsync(dto.Code, dto.Name, dto.Description, ct))));
v1.MapGet("/achievements/{id:long}", async (long id, AchievementUseCases uc, CancellationToken ct) =>
{
    var a = await uc.GetAsync(id, ct);
    return Results.Ok(AchDto(a)).WithEtag(a.Etag());
});
v1.MapPatch("/achievements/{id:long}", async (long id, AchievementPatchDto dto, AchievementUseCases uc, HttpContext http, CancellationToken ct) =>
    Results.Ok(AchDto(await uc.UpdateAsync(id, dto.Name, dto.Description, IfMatch(http), ct))));
v1.MapGet("/achievement-grants", async (MeReadUseCases uc, CancellationToken ct) =>
    Results.Ok(Page((await uc.GetMyAchievementGrantsAsync(ct)).Select(a => new AchievementGrantDto(a.EmployeeId, a.AchievementId, a.Season, a.GrantedAt)).ToList())));

// ---------- Кампании ----------
v1.MapGet("/campaigns", async (CampaignUseCases uc, int? season, string? scope, CancellationToken ct) =>
    Results.Ok(Page(new List<CampaignDto>())));
v1.MapPost("/campaigns", async (CampaignCreateDto dto, CampaignUseCases uc, CancellationToken ct) =>
{
    var c = await uc.CreateAsync(dto.Code, dto.Name, dto.Description, dto.Season, dto.Start, dto.End,
        MapAudience(dto.Audience), dto.Resources?.Select(r => (r.ResourceId, r.AllocationRequested ?? false)).ToList() ?? new(), ct);
    return Results.Created($"/api/v1/campaigns/{c.Id}", CampDto(c));
});
v1.MapGet("/campaigns/{campaignId:long}", async (long campaignId, CampaignUseCases uc, CancellationToken ct) =>
{
    var c = await uc.GetAsync(campaignId, ct);
    return Results.Ok(CampDto(c)).WithEtag(c.Etag());
});
v1.MapPatch("/campaigns/{campaignId:long}", async (long campaignId, CampaignPatchDto dto, CampaignUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var patch = new CampaignPatchData(dto.Code, dto.Name, dto.Description, dto.Season, dto.Start, dto.End, MapAudience(dto.Audience),
        dto.Resources?.Select(r => (r.ResourceId, r.AllocationRequested ?? false)).ToList(), dto.OwnerEmployeeId,
        dto.Status is null ? null : Enum.Parse<CampaignState>(dto.Status, true));
    return Results.Ok(CampDto(await uc.UpdateAsync(campaignId, patch, IfMatch(http), ct)));
});
v1.MapDelete("/campaigns/{campaignId:long}", async (long campaignId, CampaignUseCases uc, HttpContext http, CancellationToken ct) =>
{
    await uc.DeleteDraftAsync(campaignId, IfMatch(http), ct);
    return Results.NoContent();
});

// ---------- Стримы / задания / вехи / челленджи ----------
v1.MapGet("/campaigns/{campaignId:long}/streams", async (long campaignId, StreamTaskUseCases uc, CancellationToken ct) =>
    Results.Ok(Page((await uc.ListStreamsAsync(campaignId, ct)).Select(StrDto).ToList())));
v1.MapPost("/campaigns/{campaignId:long}/streams", async (long campaignId, StreamCreateDto dto, StreamTaskUseCases uc, CancellationToken ct) =>
    Results.Created($"/api/v1/streams/{0}", StrDto(await uc.CreateStreamAsync(campaignId, dto.Code, dto.Name, ct))));
v1.MapPatch("/streams/{streamId:long}", async (long streamId, StreamPatchDto dto, StreamTaskUseCases uc, HttpContext http, CancellationToken ct) =>
    Results.Ok(StrDto(await uc.UpdateStreamAsync(streamId, dto.Name, ParseStatus(dto.Status), IfMatch(http), ct))));
v1.MapGet("/streams/{streamId:long}/tasks", async (long streamId, StreamTaskUseCases uc, CancellationToken ct) =>
    Results.Ok(Page((await uc.ListTasksAsync(streamId, ct)).Select(TskDto).ToList())));
v1.MapPost("/streams/{streamId:long}/tasks", async (long streamId, TaskCreateDto dto, StreamTaskUseCases uc, CancellationToken ct) =>
    Results.Created($"/api/v1/tasks/{0}", TskDto(await uc.CreateTaskAsync(streamId, dto.Code, dto.Name, dto.Description, dto.Goal,
        ParsePeriodicity(dto.Periodicity), dto.StreamPoints, MapAudience(dto.Audience),
        dto.Rewards?.Select(r => (r.ResourceId, r.Amount)).ToList() ?? new(), ct))));
v1.MapGet("/tasks/{taskId:long}", async (long taskId, StreamTaskUseCases uc, CancellationToken ct) =>
{
    var t = await uc.GetTaskAsync(taskId, ct);
    return Results.Ok(TskDto(t)).WithEtag(t.Etag());
});
v1.MapPatch("/tasks/{taskId:long}", async (long taskId, TaskPatchDto dto, StreamTaskUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var patch = new TaskPatchData(dto.Name, dto.Description, dto.Goal, dto.Periodicity is null ? null : ParsePeriodicity(dto.Periodicity),
        dto.StreamPoints, MapAudience(dto.Audience), dto.Rewards?.Select(r => (r.ResourceId, r.Amount)).ToList(), ParseStatus(dto.Status));
    return Results.Ok(TskDto(await uc.UpdateTaskAsync(taskId, patch, IfMatch(http), ct)));
});
v1.MapGet("/streams/{streamId:long}/milestones", async (long streamId, MilestoneChallengeUseCases uc, CancellationToken ct) =>
    Results.Ok(Page((await uc.ListMilestonesAsync(streamId, ct)).Select(MilDto).ToList())));
v1.MapPost("/streams/{streamId:long}/milestones", async (long streamId, MilestoneCreateDto dto, MilestoneChallengeUseCases uc, CancellationToken ct) =>
    Results.Created($"/api/v1/milestones/{0}", MilDto(await uc.CreateMilestoneAsync(streamId, dto.Threshold, dto.AchievementId, ct))));
v1.MapPatch("/milestones/{id:long}", async (long id, MilestonePatchDto dto, MilestoneChallengeUseCases uc, HttpContext http, CancellationToken ct) =>
    Results.Ok(MilDto(await uc.UpdateMilestoneAsync(id, dto.Threshold, dto.AchievementId, IfMatch(http), ct))));
v1.MapDelete("/milestones/{id:long}", async (long id, MilestoneChallengeUseCases uc, HttpContext http, CancellationToken ct) =>
{
    await uc.DeleteMilestoneAsync(id, IfMatch(http), ct);
    return Results.NoContent();
});
v1.MapGet("/challenges", async (MilestoneChallengeUseCases uc, CancellationToken ct) =>
    Results.Ok(Page((await uc.ListChallengesAsync(ct)).Select(ChDto).ToList())));
v1.MapPost("/challenges", async (ChallengeCreateDto dto, MilestoneChallengeUseCases uc, CancellationToken ct) =>
    Results.Created($"/api/v1/challenges/{0}", ChDto(await uc.CreateChallengeAsync(dto.StreamId, dto.Start, dto.End, ct))));
v1.MapGet("/challenges/{id:long}", async (long id, MilestoneChallengeUseCases uc, CancellationToken ct) =>
{
    var ch = await uc.GetChallengeAsync(id, ct);
    return Results.Ok(ChDto(ch)).WithEtag(ch.Etag());
});
v1.MapGet("/challenges/{id:long}/leaderboard", async (long id, LeaderboardUseCases uc, int? limit, CancellationToken ct) =>
{
    var lb = await uc.GetLeaderboardAsync(id, limit ?? 50, ct);
    return Results.Ok(new LeaderboardDto(lb.Limit, lb.Top.Select(r => new RatingRowDto(r.MasterId, r.Score, r.Place)).ToList(), lb.AsOf));
});
v1.MapGet("/challenges/{id:long}/own", async (long id, LeaderboardUseCases uc, CancellationToken ct) =>
{
    var own = await uc.GetOwnRatingAsync(id, ct);
    return Results.Ok(new OwnRatingDto(own.Present, own.MasterId, own.Score, own.Place));
});

// ---------- Бюджеты ----------
v1.MapGet("/campaigns/{campaignId:long}/budgets", async (long campaignId, IUnitOfWork db, CancellationToken ct) =>
    Results.Ok(Page(new List<BudgetDto>())));
v1.MapPost("/campaigns/{campaignId:long}/budget-allocations", async (long campaignId, BudgetAllocationDto dto, EconomyUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var op = await uc.AllocateBudgetAsync(campaignId, dto.ResourceId, dto.Amount, IdemKey(http), ct);
    return Results.Created($"/api/v1/operations/{op.Id}", OutcomeDto(op));
});

// ---------- Личные чтения ----------
v1.MapGet("/me/catalog", async (MeReadUseCases uc, CancellationToken ct) =>
{
    var cat = await uc.GetMyCatalogAsync(ct);
    return Results.Ok(Page(cat.Items.Select(i => new CatalogItemDto(i.CampaignId, i.Code, i.Name, i.AsOf,
        i.Tasks.Select(t => new CatalogTaskDto(t.TaskId, t.Code, t.Name, t.Goal)).ToList())).ToList()));
});
v1.MapGet("/me/progress", async (MeReadUseCases uc, long? taskId, string? period, CancellationToken ct) =>
    Results.Ok(Page((await uc.GetMyProgressAsync(taskId, period is null ? null : ParsePeriodicity(period), ct))
        .Select(p => new ProgressDto(p.TaskId, p.Period, p.Value, p.Goal, p.Completed)).ToList())));
v1.MapGet("/me/streams/{streamId:long}/score", async (long streamId, MeReadUseCases uc, CancellationToken ct) =>
    Results.Ok(new StreamScoreDto(streamId, 0, await uc.GetMyStreamScoreAsync(streamId, ct))));
v1.MapGet("/me/tasks/{taskId:long}/reward", async (long taskId, MeReadUseCases uc, string? period, CancellationToken ct) =>
{
    var r = await uc.GetMyRewardAsync(taskId, period is null ? null : ParsePeriodicity(period), ct);
    return Results.Ok(new RewardDto(r.TaskId, r.Result, r.Lines?.Select(l => new RewardLineDto(l.ResourceId, l.Amount)).ToList(), r.RejectReason));
});

// ---------- Кошелёк ----------
v1.MapGet("/wallet", async (WalletReadUseCases uc, CancellationToken ct) =>
{
    var w = await uc.GetWalletAsync(ct);
    return Results.Ok(new WalletDto(w.EmployeeId, w.Balances.Select(b => new BalanceDto(b.ResourceId, b.Code, b.Balance)).ToList()));
});
v1.MapGet("/wallet/movements", async (WalletReadUseCases uc, long? resource, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
    Results.Ok(Page((await uc.ListMovementsAsync(resource, from, to, ct)).Select(m =>
        new MovementDto(m.Id, m.OperationId, m.ResourceId, m.Delta, m.BalanceAfter, m.OccurredAt, m.Kind.ToString(), m.CampaignId, m.OriginalOperationId)).ToList())));
v1.MapGet("/wallet/operations", async (WalletReadUseCases uc, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
    Results.Ok(Page((await uc.ListWalletOperationsAsync(from, to, ct)).Select(OpDto).ToList())));
v1.MapGet("/operations/{operationId:long}", async (long operationId, WalletReadUseCases uc, CancellationToken ct) =>
    Results.Ok(OpDto(await uc.GetOperationAsync(operationId, ct))));

// ---------- Экономические операции ----------
v1.MapPost("/spends", async (SpendRequestDto dto, EconomyUseCases uc, IUnitOfWork db, HttpContext http, CancellationToken ct) =>
{
    var actor = http.Items["actor"] as Actor
        ?? throw MotivaException.Forbidden("Требуется аутентификация");
    var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.CompanyId == actor.CompanyId && e.MasterId == actor.MasterId, ct)
        ?? throw MotivaException.NotFound("Профиль не найден");
    var op = await uc.CreateSpendAsync(employee.Id, dto.ResourceId, dto.Amount, dto.PurchaseSystemId, dto.Basis, IdemKey(http), ct);
    return Results.Created($"/api/v1/operations/{op.Id}", OutcomeDto(op));
});
v1.MapPost("/manual-accruals", async (ManualAccrualRequestDto dto, EconomyUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var op = await uc.ManualAccrualAsync(dto.CampaignId, dto.EmployeeId, dto.ResourceId, dto.Amount, dto.Basis, IdemKey(http), ct);
    return Results.Created($"/api/v1/operations/{op.Id}", OutcomeDto(op));
});
v1.MapPost("/accrual-cancellations", async (CancellationRequestDto dto, EconomyUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var op = await uc.CancelAccrualAsync(dto.OriginalOperationId, dto.Basis, IdemKey(http), ct);
    return Results.Created($"/api/v1/operations/{op.Id}", OutcomeDto(op));
});
v1.MapPost("/spend-refunds", async (CancellationRequestDto dto, EconomyUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var op = await uc.RefundSpendAsync(dto.OriginalOperationId, dto.Basis, IdemKey(http), ct);
    return Results.Created($"/api/v1/operations/{op.Id}", OutcomeDto(op));
});

// ---------- Интеграции (internal) ----------
internal_.MapPost("/events", async (EventRequestDto dto, EconomyUseCases uc, CancellationToken ct) =>
{
    var r = await uc.TransmitProgressAsync(dto.EmployeeId, dto.TaskId, dto.Increase, dto.SourceEventNo, ct);
    return Results.Created("/internal/v1/events",
        new EventResultDto(r.Accepted, r.CreditedIncrease, r.Completed, r.RewardResult is null ? null : new EventRewardDto(r.RewardResult, r.RewardReason)));
});
internal_.MapPost("/spends", async (InternalSpendRequestDto dto, EconomyUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var op = await uc.IntegrationSpendAsync(dto.EmployeeId, dto.ResourceId, dto.Amount, dto.PurchaseSystemId, dto.Basis, IdemKey(http), ct);
    return Results.Created($"/internal/v1/operations/{op.Id}", OutcomeDto(op));
});
internal_.MapPost("/manual-accruals", async (InternalManualAccrualRequestDto dto, EconomyUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var op = await uc.IntegrationManualAccrualAsync(dto.CampaignId, dto.EmployeeId, dto.ResourceId, dto.Amount, dto.Basis, IdemKey(http), ct);
    return Results.Created($"/internal/v1/operations/{op.Id}", OutcomeDto(op));
});
internal_.MapPost("/spends/{operationId:long}/refund", async (long operationId, InternalRefundRequestDto dto, EconomyUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var op = await uc.RefundSpendAsync(operationId, dto.Basis, IdemKey(http), ct);
    return Results.Created($"/internal/v1/operations/{op.Id}", OutcomeDto(op));
});
internal_.MapGet("/operations", async (WalletReadUseCases uc, CancellationToken ct) =>
    Results.Ok(Page((await uc.ListIntegrationOperationsAsync(ct)).Select(OpDto).ToList())));

// ---------- Выписки ----------
v1.MapPost("/statements", async (StatementRequestDto dto, StatementUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var s = await uc.CreateAsync(dto.EmployeeId, dto.From, dto.To, dto.ResourceId, IdemKey(http), ct);
    return Results.Created($"/api/v1/statements/{s.Id}", StmtDto(s));
});
v1.MapGet("/statements", async (StatementUseCases uc, CancellationToken ct) =>
    Results.Ok(Page((await uc.ListAsync(ct)).Select(StmtDto).ToList())));
v1.MapGet("/statements/{id:long}", async (long id, StatementUseCases uc, CancellationToken ct) =>
    Results.Ok(StmtDto(await uc.GetAsync(id, ct))));
v1.MapDelete("/statements/{id:long}", async (long id, StatementUseCases uc, CancellationToken ct) =>
{
    await uc.DeleteAsync(id, ct);
    return Results.NoContent();
});
v1.MapPost("/statements/{id:long}/download-links", async (long id, StatementUseCases uc, HttpContext http, CancellationToken ct) =>
{
    var link = await uc.CreateDownloadLinkAsync(id, IdemKey(http), ct);
    return Results.Created($"/api/v1/statements/{id}/download-links", new DownloadLinkDto(link.Url, link.ExpiresAt));
});

// ---------- Аудит ----------
v1.MapGet("/audit", async (AuditUseCases uc, CancellationToken ct) =>
    Results.Ok(Page((await uc.ListAsync(ct)).Select(a => new AuditEntryDto(a.Id, a.Actor, a.Target, a.Action.ToString(), a.OccurredAt, a.Details)).ToList())));

app.Run();

// ---------- Helpers ----------
static PageResponse<T> Page<T>(List<T> items) => new(items, null);
static Actor? ActorFrom(ClaimsPrincipal user)
{
    var sub = user.FindFirstValue("sub");
    var company = user.FindFirstValue("companyId");
    var actorType = user.FindFirstValue("actorType");
    var masterRaw = user.FindFirstValue("masterId");
    var roles = user.FindAll("role").Select(r => r.Value).ToList();
    if (sub is null || company is null || actorType is null || !Guid.TryParse(company, out var companyId))
    {
        return null;
    }

    int? masterId = null;
    if (actorType == "user")
    {
        if (!int.TryParse(masterRaw, out var parsed) || parsed <= 0)
        {
            return null;
        }

        masterId = parsed;
    }
    return new Actor(actorType == "service" ? ActorType.Service : ActorType.User, sub, companyId,
        masterId, roles.Contains("Admin"));
}
static string? IfMatch(HttpContext http) => http.Request.Headers.IfMatch.FirstOrDefault();
static string IdemKey(HttpContext http) => http.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "";
static ResourceStatus? ParseStatus(string? s) => s is null ? null : Enum.Parse<ResourceStatus>(s, true);
static Periodicity ParsePeriodicity(string s) => Enum.Parse<Periodicity>(s, true);
static GrantKind ParseGrant(string s) => Enum.Parse<GrantKind>(s, true);
static Audience MapAudience(AudienceDto? d) => d is null
        ? new Audience()
        : new Audience { Any = d.Any ?? new(), All = d.All ?? new(), None = d.None ?? new() };

static EmployeeDto EmpDto(Employee e) => new(e.Id, e.MasterId, e.IsActive, e.Tags, e.Etag());
static ResourceDto ResDto(Resource r) => new(r.Id, r.Code, r.Name, r.Status.ToString().ToLower(), r.Etag());
static GrantDto ToGrantDto(IntegrationGrant g) => new(g.Kind.ToString(), g.CampaignId, g.ResourceId, g.PurchaseSystemId);
static PurchaseSystemDto PsDto(PurchaseSystem p) => new(p.Id, p.Name, p.ResourceIds, p.Etag());
static AchievementDto AchDto(Achievement a) => new(a.Id, a.Code, a.Name, a.Description, a.Etag());
static CampaignDto CampDto(Campaign c) => new(c.Id, c.Code, c.Name, c.Description, c.Season, c.Start, c.End, c.State.ToString().ToLower(),
    c.OwnerEmployeeId, new AudienceDto(c.Audience.Any, c.Audience.All, c.Audience.None),
    c.Resources?.Select(r => new CampaignResourceDto(r.ResourceId, r.AllocationRequested)).ToList(), c.Etag());
static StreamDto StrDto(CampaignStream s) => new(s.Id, s.CampaignId, s.Code, s.Name, null, s.Status.ToString().ToLower(), s.Etag());
static TaskDto TskDto(CampaignTask t) => new(t.Id, t.StreamId, t.Code, t.Name, t.Description, t.Goal, t.Periodicity.ToString().ToLower(),
    t.StreamPoints, new AudienceDto(t.Audience.Any, t.Audience.All, t.Audience.None),
    t.Rewards?.Select(r => new RewardLineDto(r.ResourceId, r.Amount)).ToList(), t.Status.ToString().ToLower(), t.Etag());
static MilestoneDto MilDto(Milestone m) => new(m.Id, m.StreamId, m.Threshold, m.AchievementId, m.Etag());
static ChallengeDto ChDto(Challenge c) => new(c.Id, c.StreamId, c.Start, c.End, c.State.ToString().ToLower(), c.Etag());
static StatementDto StmtDto(Statement s) => new(s.Id, s.State.ToString().ToLower(), s.Scope.ToString().ToLower(), s.From, s.To, s.ResourceId, s.Etag());

static OperationDto OpDto(Operation o) => new(o.Id, o.Kind.ToString(), o.InitiatorKey, o.SourceNo, o.Basis, o.EmployeeId,
    o.Result.ToString().ToLower(), o.RejectReason, o.OccurredAt, o.CampaignId, o.PurchaseSystemId, o.OriginalOperationId,
    o.Lines?.Select(l => new OperationLineDto(l.ResourceId, l.Amount)).ToList());
static OutcomeDto OutcomeDto(Operation o) => new(o.Id, o.Result.ToString().ToLower(), o.RejectReason);

public partial class Program { }
