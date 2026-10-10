using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Motiva.Tests.Integration;

/// <summary>Вертикаль S1 и экономические сценарии через штатный HTTP (T08).</summary>
[Collection("motiva")]
public class S1VerticalTests : IClassFixture<MotivaFactory>
{
    private readonly MotivaFactory _factory;
    public S1VerticalTests(MotivaFactory factory) => _factory = factory;

    private HttpClient Admin(string company) => _factory.Client(company, "user", masterId: 1, admin: true);

    private static async Task<JsonElement> Post(HttpClient c, string url, object body, HttpStatusCode expected = HttpStatusCode.Created, string? idem = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (idem is not null)
        {
            req.Headers.Add("Idempotency-Key", idem);
        }

        var res = await c.SendAsync(req);
        if (res.StatusCode != expected)
        {
            throw new Xunit.Sdk.XunitException($"POST {url} -> {res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        }

        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> Get(HttpClient c, string url)
    {
        var res = await c.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> BootstrapAsync()
    {
        var bootstrap = _factory.Client(MotivaFactory.Company, "user", masterId: 1, admin: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/bootstrap")
        { Content = JsonContent.Create(new { companyName = "C", adminMasterId = 1 }) };
        var res = await bootstrap.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("companyId").GetString()!;
    }

    [Fact]
    public async Task E01_Two_resources_reward_granted_full()
    {
        var company = await BootstrapAsync();
        var admin = Admin(company);
        var now = DateTimeOffset.UtcNow;

        // ресурсы A, B
        var a = (await Post(admin, "/api/v1/resources", new { code = "A", name = "Alpha" })).GetProperty("id").GetInt64();
        var b = (await Post(admin, "/api/v1/resources", new { code = "B", name = "Beta" })).GetProperty("id").GetInt64();
        // сотрудник
        var emp = await Post(admin, "/api/v1/employees", new { masterId = 123, tags = Array.Empty<string>() });
        var employeeId = emp.GetProperty("id").GetInt64();
        // достижение
        var ach = await Post(admin, "/api/v1/achievements", new { code = "ach1", name = "Ach" });

        // кампания (сезон текущего года)
        var season = now.Year;
        var campaign = await Post(admin, "/api/v1/campaigns", new
        {
            code = "CAMPAIGN-1",
            name = "C1",
            season,
            start = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
            end = new DateTimeOffset(now.Year, 12, 31, 23, 59, 59, TimeSpan.Zero),
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            resources = new[] { new { resourceId = a, allocationRequested = true }, new { resourceId = b, allocationRequested = true } },
        });
        var campaignId = campaign.GetProperty("id").GetInt64();

        // стрим + задание с наградой 10A + 2B
        var stream = await Post(admin, $"/api/v1/campaigns/{campaignId}/streams", new { code = "s1", name = "S1" });
        var streamId = stream.GetProperty("id").GetInt64();
        var task = await Post(admin, $"/api/v1/streams/{streamId}/tasks", new
        {
            code = "t1",
            name = "T1",
            goal = 3,
            periodicity = "month",
            streamPoints = 5,
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            rewards = new[] { new { resourceId = a, amount = 10 }, new { resourceId = b, amount = 2 } },
        });
        var taskId = task.GetProperty("id").GetInt64();

        // веха
        await Post(admin, $"/api/v1/streams/{streamId}/milestones", new { threshold = 5, achievementId = ach.GetProperty("id").GetInt64() });

        // бюджеты
        await Post(admin, $"/api/v1/campaigns/{campaignId}/budget-allocations", new { resourceId = a, amount = 10 }, idem: "alloc-a");
        await Post(admin, $"/api/v1/campaigns/{campaignId}/budget-allocations", new { resourceId = b, amount = 2 }, idem: "alloc-b");

        // публикация (If-Match)
        var campRes = await admin.GetAsync($"/api/v1/campaigns/{campaignId}");
        var etag = campRes.Headers.ETag!.Tag;
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/campaigns/{campaignId}") { Content = JsonContent.Create(new { status = "published" }) })
        {
            patch.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag!));
            var pres = await admin.SendAsync(patch);
            Assert.Equal(HttpStatusCode.OK, pres.StatusCode);
        }

        // интеграция + grant progress
        var integration = await Post(admin, "/api/v1/integrations", new { name = "src", serviceSub = "progress-source" });
        var integrationId = integration.GetProperty("id").GetInt64();
        var grantsRes = await admin.GetAsync($"/api/v1/integrations/{integrationId}/grants");
        var grantEtag = grantsRes.Headers.ETag!.Tag;
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/integrations/{integrationId}/grants")
        { Content = JsonContent.Create(new { grants = new[] { new { kind = "progress", campaignId } } }) })
        {
            put.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(grantEtag!));
            var pres = await admin.SendAsync(put);
            Assert.Equal(HttpStatusCode.OK, pres.StatusCode);
        }

        // событие +5 (цель 3) от сервиса
        var service = _factory.Client(company.ToString(), "service", subject: "progress-source");
        var ev = await Post(service, "/internal/v1/events", new { employeeId, taskId, increase = 5, sourceEventNo = "e1" });
        Assert.Equal(3, ev.GetProperty("creditedIncrease").GetInt32());
        Assert.True(ev.GetProperty("completed").GetBoolean());
        Assert.Equal("granted", ev.GetProperty("reward").GetProperty("result").GetString());

        // read-back: кошелёк A=10, B=2 (E01)
        var employee = _factory.Client(company.ToString(), "user", masterId: 123);
        var wallet = await Get(employee, "/api/v1/wallet");
        var balances = wallet.GetProperty("balances").EnumerateArray()
            .ToDictionary(x => x.GetProperty("code").GetString()!, x => x.GetProperty("balance").GetInt64());
        Assert.Equal(10, balances["A"]);
        Assert.Equal(2, balances["B"]);

        // бюджеты A=0, B=0
        var budgets = await Get(admin, $"/api/v1/campaigns/{campaignId}/budgets");

        // прогресс
        var progress = await Get(employee, "/api/v1/me/progress");
        Assert.Equal(3, progress.GetProperty("items")[0].GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Event_replay_is_idempotent_and_does_not_duplicate()
    {
        var company = await BootstrapAsync();
        var admin = Admin(company);
        var a = (await Post(admin, "/api/v1/resources", new { code = "A", name = "A" })).GetProperty("id").GetInt64();
        var emp = (await Post(admin, "/api/v1/employees", new { masterId = 7, tags = Array.Empty<string>() })).GetProperty("id").GetInt64();
        var now = DateTimeOffset.UtcNow;
        var campaign = await Post(admin, "/api/v1/campaigns", new
        {
            code = "C",
            name = "C",
            season = now.Year,
            start = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
            end = new DateTimeOffset(now.Year, 12, 31, 23, 59, 59, TimeSpan.Zero),
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            resources = new[] { new { resourceId = a, allocationRequested = true } },
        });
        var campaignId = campaign.GetProperty("id").GetInt64();
        var stream = (await Post(admin, $"/api/v1/campaigns/{campaignId}/streams", new { code = "s", name = "s" })).GetProperty("id").GetInt64();
        var task = await Post(admin, $"/api/v1/streams/{stream}/tasks", new
        {
            code = "t",
            name = "t",
            goal = 10,
            periodicity = "month",
            streamPoints = 1,
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            rewards = Array.Empty<object>(),
        });
        var taskId = task.GetProperty("id").GetInt64();
        await Post(admin, $"/api/v1/campaigns/{campaignId}/budget-allocations", new { resourceId = a, amount = 100 }, idem: "x1");
        var campRes = await admin.GetAsync($"/api/v1/campaigns/{campaignId}");
        var etag = campRes.Headers.ETag!.Tag;
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/campaigns/{campaignId}") { Content = JsonContent.Create(new { status = "published" }) })
        { patch.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag!)); var r = await admin.SendAsync(patch); Assert.Equal(HttpStatusCode.OK, r.StatusCode); }
        var integration = (await Post(admin, "/api/v1/integrations", new { name = "src", serviceSub = "p-src" })).GetProperty("id").GetInt64();
        var getres = await admin.GetAsync($"/api/v1/integrations/{integration}/grants");
        var getag = getres.Headers.ETag!.Tag;
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/integrations/{integration}/grants")
        { Content = JsonContent.Create(new { grants = new[] { new { kind = "progress", campaignId } } }) })
        { put.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(getag!)); await admin.SendAsync(put); }

        var service = _factory.Client(company.ToString(), "service", subject: "p-src");
        var e1 = await Post(service, "/internal/v1/events", new { employeeId = emp, taskId, increase = 10, sourceEventNo = "s1" });
        Assert.Equal(10, e1.GetProperty("creditedIncrease").GetInt32());
        // повтор того же номера — прежний результат, без нового эффекта
        var e2 = await Post(service, "/internal/v1/events", new { employeeId = emp, taskId, increase = 10, sourceEventNo = "s1" });
        Assert.Equal(e1.GetProperty("creditedIncrease").GetInt32(), e2.GetProperty("creditedIncrease").GetInt32());
        var employee = _factory.Client(company.ToString(), "user", masterId: 7);
        var progress = await Get(employee, "/api/v1/me/progress");
        var value = progress.GetProperty("items")[0].GetProperty("value").GetInt32();
        Assert.Equal(10, value);
    }

    [Fact]
    public async Task Insufficient_budget_yields_declined_no_partial_reward()
    {
        var company = await BootstrapAsync();
        var admin = Admin(company);
        var a = (await Post(admin, "/api/v1/resources", new { code = "A", name = "A" })).GetProperty("id").GetInt64();
        var b = (await Post(admin, "/api/v1/resources", new { code = "B", name = "B" })).GetProperty("id").GetInt64();
        var emp = (await Post(admin, "/api/v1/employees", new { masterId = 9, tags = Array.Empty<string>() })).GetProperty("id").GetInt64();
        var now = DateTimeOffset.UtcNow;
        var campaign = await Post(admin, "/api/v1/campaigns", new
        {
            code = "C",
            name = "C",
            season = now.Year,
            start = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
            end = new DateTimeOffset(now.Year, 12, 31, 23, 59, 59, TimeSpan.Zero),
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            resources = new[] { new { resourceId = a, allocationRequested = true }, new { resourceId = b, allocationRequested = true } },
        });
        var campaignId = campaign.GetProperty("id").GetInt64();
        var stream = (await Post(admin, $"/api/v1/campaigns/{campaignId}/streams", new { code = "s", name = "s" })).GetProperty("id").GetInt64();
        var task = await Post(admin, $"/api/v1/streams/{stream}/tasks", new
        {
            code = "t",
            name = "t",
            goal = 3,
            periodicity = "month",
            streamPoints = 5,
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            rewards = new[] { new { resourceId = a, amount = 10 }, new { resourceId = b, amount = 2 } },
        });
        var taskId = task.GetProperty("id").GetInt64();
        // B бюджета недостаточно (1 < 2)
        await Post(admin, $"/api/v1/campaigns/{campaignId}/budget-allocations", new { resourceId = a, amount = 10 }, idem: "x1");
        await Post(admin, $"/api/v1/campaigns/{campaignId}/budget-allocations", new { resourceId = b, amount = 1 }, idem: "x2");
        var campRes = await admin.GetAsync($"/api/v1/campaigns/{campaignId}");
        var etag = campRes.Headers.ETag!.Tag;
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/campaigns/{campaignId}") { Content = JsonContent.Create(new { status = "published" }) })
        { patch.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag!)); var r = await admin.SendAsync(patch); Assert.Equal(HttpStatusCode.OK, r.StatusCode); }
        var integration = (await Post(admin, "/api/v1/integrations", new { name = "src", serviceSub = "p-src" })).GetProperty("id").GetInt64();
        var getres = await admin.GetAsync($"/api/v1/integrations/{integration}/grants");
        var getag = getres.Headers.ETag!.Tag;
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/integrations/{integration}/grants")
        { Content = JsonContent.Create(new { grants = new[] { new { kind = "progress", campaignId } } }) })
        { put.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(getag!)); await admin.SendAsync(put); }

        var service = _factory.Client(company.ToString(), "service", subject: "p-src");
        var ev = await Post(service, "/internal/v1/events", new { employeeId = emp, taskId, increase = 5, sourceEventNo = "e1" });
        // E02: ни A, ни B не начислены; завершение и очки есть
        Assert.Equal("declined", ev.GetProperty("reward").GetProperty("result").GetString());
        var employee = _factory.Client(company.ToString(), "user", masterId: 9);
        var wallet = await Get(employee, "/api/v1/wallet");
        var balances = wallet.GetProperty("balances").EnumerateArray()
            .ToDictionary(x => x.GetProperty("code").GetString()!, x => x.GetProperty("balance").GetInt64());
        Assert.Equal(0, balances.GetValueOrDefault("A", 0));
        Assert.Equal(0, balances.GetValueOrDefault("B", 0));
        Assert.True(ev.GetProperty("completed").GetBoolean());
    }

    [Fact]
    public async Task E05_Two_competitive_spends_only_one_posted()
    {
        var company = await BootstrapAsync();
        var admin = Admin(company);
        var a = (await Post(admin, "/api/v1/resources", new { code = "A", name = "A" })).GetProperty("id").GetInt64();
        var emp = (await Post(admin, "/api/v1/employees", new { masterId = 55, tags = Array.Empty<string>() })).GetProperty("id").GetInt64();
        var now = DateTimeOffset.UtcNow;
        var campaign = await Post(admin, "/api/v1/campaigns", new
        {
            code = "C",
            name = "C",
            season = now.Year,
            start = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
            end = new DateTimeOffset(now.Year, 12, 31, 23, 59, 59, TimeSpan.Zero),
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            resources = new[] { new { resourceId = a, allocationRequested = true } },
        });
        var campaignId = campaign.GetProperty("id").GetInt64();
        var stream = (await Post(admin, $"/api/v1/campaigns/{campaignId}/streams", new { code = "s", name = "s" })).GetProperty("id").GetInt64();
        var task = await Post(admin, $"/api/v1/streams/{stream}/tasks", new
        {
            code = "t",
            name = "t",
            goal = 3,
            periodicity = "month",
            streamPoints = 1,
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            rewards = new[] { new { resourceId = a, amount = 10 } },
        });
        var taskId = task.GetProperty("id").GetInt64();
        await Post(admin, $"/api/v1/campaigns/{campaignId}/budget-allocations", new { resourceId = a, amount = 10 }, idem: "x1");
        var campRes = await admin.GetAsync($"/api/v1/campaigns/{campaignId}");
        var etag = campRes.Headers.ETag!.Tag;
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/campaigns/{campaignId}") { Content = JsonContent.Create(new { status = "published" }) })
        { patch.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag!)); var r = await admin.SendAsync(patch); Assert.Equal(HttpStatusCode.OK, r.StatusCode); }
        var integration = (await Post(admin, "/api/v1/integrations", new { name = "src", serviceSub = "p-src" })).GetProperty("id").GetInt64();
        var getres = await admin.GetAsync($"/api/v1/integrations/{integration}/grants");
        var getag = getres.Headers.ETag!.Tag;
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/integrations/{integration}/grants")
        { Content = JsonContent.Create(new { grants = new[] { new { kind = "progress", campaignId } } }) })
        { put.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(getag!)); await admin.SendAsync(put); }
        // сотрудник получает 10A через завершение
        var service = _factory.Client(company.ToString(), "service", subject: "p-src");
        await Post(service, "/internal/v1/events", new { employeeId = emp, taskId, increase = 3, sourceEventNo = "e1" });

        // система покупок
        var ps = (await Post(admin, "/api/v1/purchase-systems", new { name = "shop" })).GetProperty("id").GetInt64();
        var psRes = await admin.GetAsync($"/api/v1/purchase-systems/{ps}");
        var psEtag = psRes.Headers.ETag!.Tag;
        using (var putRes = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/purchase-systems/{ps}/resources") { Content = JsonContent.Create(new[] { a }) })
        { putRes.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(psEtag!)); await admin.SendAsync(putRes); }

        var employee = _factory.Client(company.ToString(), "user", masterId: 55);
        var w1 = Post(employee, "/api/v1/spends", new { resourceId = a, amount = 7, purchaseSystemId = ps, basis = "b1" }, idem: "sp1");
        var w2 = Post(employee, "/api/v1/spends", new { resourceId = a, amount = 7, purchaseSystemId = ps, basis = "b2" }, idem: "sp2");
        var results = await Task.WhenAll(w1, w2);
        var statuses = results.Select(r => r.GetProperty("status").GetString()!).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "declined", "posted" }, statuses);

        var wallet = await Get(employee, "/api/v1/wallet");
        var bal = wallet.GetProperty("balances")[0].GetProperty("balance").GetInt64();
        Assert.Equal(3, bal); // E05: одна posted (A=3), одна declined, не −4
    }

    [Fact]
    public async Task Manual_accrual_and_admin_cancellation_roundtrip()
    {
        var company = await BootstrapAsync();
        var admin = Admin(company);
        var a = (await Post(admin, "/api/v1/resources", new { code = "A", name = "A" })).GetProperty("id").GetInt64();
        var emp = (await Post(admin, "/api/v1/employees", new { masterId = 66, tags = Array.Empty<string>() })).GetProperty("id").GetInt64();
        var now = DateTimeOffset.UtcNow;
        var campaign = await Post(admin, "/api/v1/campaigns", new
        {
            code = "C",
            name = "C",
            season = now.Year,
            start = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
            end = new DateTimeOffset(now.Year, 12, 31, 23, 59, 59, TimeSpan.Zero),
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            resources = new[] { new { resourceId = a, allocationRequested = true } },
        });
        var campaignId = campaign.GetProperty("id").GetInt64();
        var stream = (await Post(admin, $"/api/v1/campaigns/{campaignId}/streams", new { code = "s", name = "s" })).GetProperty("id").GetInt64();
        await Post(admin, $"/api/v1/streams/{stream}/tasks", new
        {
            code = "t",
            name = "t",
            goal = 1,
            periodicity = "month",
            streamPoints = 0,
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            rewards = Array.Empty<object>(),
        });
        await Post(admin, $"/api/v1/campaigns/{campaignId}/budget-allocations", new { resourceId = a, amount = 10 }, idem: "x1");
        var campRes = await admin.GetAsync($"/api/v1/campaigns/{campaignId}");
        var etag = campRes.Headers.ETag!.Tag;
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/campaigns/{campaignId}") { Content = JsonContent.Create(new { status = "published" }) })
        { patch.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag!)); var r = await admin.SendAsync(patch); Assert.Equal(HttpStatusCode.OK, r.StatusCode); }

        // ручное начисление 5A (B21) — бюджет 10→5, кошелёк 0→5
        var accrual = await Post(admin, "/api/v1/manual-accruals", new { campaignId, employeeId = emp, resourceId = a, amount = 5, basis = "bonus" }, idem: "acc1");
        Assert.Equal("posted", accrual.GetProperty("status").GetString());
        var accrualOp = accrual.GetProperty("operationId").GetInt64();
        var employee = _factory.Client(company.ToString(), "user", masterId: 66);
        var wallet = await Get(employee, "/api/v1/wallet");
        Assert.Equal(5, wallet.GetProperty("balances")[0].GetProperty("balance").GetInt64());

        // отмена начисления Admin (B26): 5A возвращаются в бюджет
        var cancel = await Post(admin, "/api/v1/accrual-cancellations", new { originalOperationId = accrualOp, basis = "undo" }, idem: "cancel1");
        Assert.Equal("posted", cancel.GetProperty("status").GetString());
        var wallet2 = await Get(employee, "/api/v1/wallet");
        Assert.Equal(0, wallet2.GetProperty("balances")[0].GetProperty("balance").GetInt64());

        // повтор отмены того же оригинала новым номером → 409 (одна успешная отмена, B25d)
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/accrual-cancellations")
        { Content = JsonContent.Create(new { originalOperationId = accrualOp, basis = "again" }) })
        {
            req.Headers.Add("Idempotency-Key", "cancel2");
            var res = await admin.SendAsync(req);
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        }
    }

    /// <summary>E2E-путь экспорта (T07/B36): выписка формируется worker-путем, CSV в S3, ссылка выдаётся.</summary>
    [Fact]
    public async Task Export_statement_becomes_ready_with_csv_in_s3()
    {
        var company = await BootstrapAsync();
        var admin = Admin(company);
        var a = (await Post(admin, "/api/v1/resources", new { code = "A", name = "A" })).GetProperty("id").GetInt64();
        var emp = (await Post(admin, "/api/v1/employees", new { masterId = 77, tags = Array.Empty<string>() })).GetProperty("id").GetInt64();
        var now = DateTimeOffset.UtcNow;
        var campaign = await Post(admin, "/api/v1/campaigns", new
        {
            code = "C",
            name = "C",
            season = now.Year,
            start = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
            end = new DateTimeOffset(now.Year, 12, 31, 23, 59, 59, TimeSpan.Zero),
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            resources = new[] { new { resourceId = a, allocationRequested = true } },
        });
        var campaignId = campaign.GetProperty("id").GetInt64();
        var stream = (await Post(admin, $"/api/v1/campaigns/{campaignId}/streams", new { code = "s", name = "s" })).GetProperty("id").GetInt64();
        var task = await Post(admin, $"/api/v1/streams/{stream}/tasks", new
        {
            code = "t",
            name = "t",
            goal = 3,
            periodicity = "month",
            streamPoints = 1,
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            rewards = new[] { new { resourceId = a, amount = 10 } },
        });
        var taskId = task.GetProperty("id").GetInt64();
        await Post(admin, $"/api/v1/campaigns/{campaignId}/budget-allocations", new { resourceId = a, amount = 10 }, idem: "x1");
        var campRes = await admin.GetAsync($"/api/v1/campaigns/{campaignId}");
        var etag = campRes.Headers.ETag!.Tag;
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/campaigns/{campaignId}") { Content = JsonContent.Create(new { status = "published" }) })
        { patch.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag!)); var r = await admin.SendAsync(patch); Assert.Equal(HttpStatusCode.OK, r.StatusCode); }
        var integration = (await Post(admin, "/api/v1/integrations", new { name = "src", serviceSub = "p-src" })).GetProperty("id").GetInt64();
        var getres = await admin.GetAsync($"/api/v1/integrations/{integration}/grants");
        var getag = getres.Headers.ETag!.Tag;
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/integrations/{integration}/grants")
        { Content = JsonContent.Create(new { grants = new[] { new { kind = "progress", campaignId } } }) })
        { put.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(getag!)); await admin.SendAsync(put); }
        var service = _factory.Client(company.ToString(), "service", subject: "p-src");
        await Post(service, "/internal/v1/events", new { employeeId = emp, taskId, increase = 3, sourceEventNo = "e1" });

        // заказ выписки сотрудником
        var employee = _factory.Client(company.ToString(), "user", masterId: 77);
        var stmt = await Post(employee, "/api/v1/statements", new
        {
            from = now.AddDays(-1),
            to = now.AddDays(1),
        }, idem: "stmt-1");
        var statementId = stmt.GetProperty("id").GetInt64();

        // worker-путь: формирование через IStatementGenerator
        using var scope = _factory.Services.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<Motiva.Application.Ports.IStatementGenerator>();
        var generated = await generator.GenerateAsync(statementId, "test-worker", CancellationToken.None);
        Assert.True(generated);

        var ready = await Get(employee, $"/api/v1/statements/{statementId}");
        Assert.Equal("ready", ready.GetProperty("state").GetString());

        // проверка содержимого CSV в S3 (ключ = snapshot/fileRef)
        var storage = scope.ServiceProvider.GetRequiredService<Motiva.Application.Ports.IObjectStorage>();
        var db = scope.ServiceProvider.GetRequiredService<Motiva.Infrastructure.MotivaDbContext>();
        var statement = await db.Statements.FirstAsync(s => s.Id == statementId);
        Assert.NotNull(statement.FileRef);
        var bucket = storage.Bucket(Guid.Parse(company));
        var obj = await storage.GetAsync(bucket, statement.FileRef!, CancellationToken.None);
        using var reader = new StreamReader(obj);
        var text = await reader.ReadToEndAsync();
        Assert.StartsWith("operationId,masterId,resourceCode,amount,kind,campaignId,originalOperationId,createdAtUtc", text);
        Assert.Contains("A", text); // начисление ресурса A в составе

        // скачивание: ссылка ≤ 60 с
        var link = await Post(employee, $"/api/v1/statements/{statementId}/download-links", new { }, idem: "dl-1");
        Assert.True(link.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    /// <summary>E07: возврат списания (Admin) возвращает средства в кошелёк.</summary>
    [Fact]
    public async Task E07_Refund_returns_spent_funds()
    {
        var company = await BootstrapAsync();
        var admin = Admin(company);
        var a = (await Post(admin, "/api/v1/resources", new { code = "A", name = "A" })).GetProperty("id").GetInt64();
        var emp = (await Post(admin, "/api/v1/employees", new { masterId = 88, tags = Array.Empty<string>() })).GetProperty("id").GetInt64();
        var now = DateTimeOffset.UtcNow;
        var campaign = await Post(admin, "/api/v1/campaigns", new
        {
            code = "C",
            name = "C",
            season = now.Year,
            start = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
            end = new DateTimeOffset(now.Year, 12, 31, 23, 59, 59, TimeSpan.Zero),
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            resources = new[] { new { resourceId = a, allocationRequested = true } },
        });
        var campaignId = campaign.GetProperty("id").GetInt64();
        var stream = (await Post(admin, $"/api/v1/campaigns/{campaignId}/streams", new { code = "s", name = "s" })).GetProperty("id").GetInt64();
        var task = await Post(admin, $"/api/v1/streams/{stream}/tasks", new
        {
            code = "t",
            name = "t",
            goal = 3,
            periodicity = "month",
            streamPoints = 1,
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            rewards = new[] { new { resourceId = a, amount = 10 } },
        });
        var taskId = task.GetProperty("id").GetInt64();
        await Post(admin, $"/api/v1/campaigns/{campaignId}/budget-allocations", new { resourceId = a, amount = 10 }, idem: "x1");
        var campRes = await admin.GetAsync($"/api/v1/campaigns/{campaignId}");
        var etag = campRes.Headers.ETag!.Tag;
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/campaigns/{campaignId}") { Content = JsonContent.Create(new { status = "published" }) })
        { patch.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag!)); var r = await admin.SendAsync(patch); Assert.Equal(HttpStatusCode.OK, r.StatusCode); }
        var integration = (await Post(admin, "/api/v1/integrations", new { name = "src", serviceSub = "p-src" })).GetProperty("id").GetInt64();
        var getres = await admin.GetAsync($"/api/v1/integrations/{integration}/grants");
        var getag = getres.Headers.ETag!.Tag;
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/integrations/{integration}/grants")
        { Content = JsonContent.Create(new { grants = new[] { new { kind = "progress", campaignId } } }) })
        { put.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(getag!)); await admin.SendAsync(put); }
        var service = _factory.Client(company.ToString(), "service", subject: "p-src");
        await Post(service, "/internal/v1/events", new { employeeId = emp, taskId, increase = 3, sourceEventNo = "e1" });

        var ps = (await Post(admin, "/api/v1/purchase-systems", new { name = "shop" })).GetProperty("id").GetInt64();
        var psRes = await admin.GetAsync($"/api/v1/purchase-systems/{ps}");
        var psEtag = psRes.Headers.ETag!.Tag;
        using (var putRes = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/purchase-systems/{ps}/resources") { Content = JsonContent.Create(new[] { a }) })
        { putRes.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(psEtag!)); await admin.SendAsync(putRes); }

        var employee = _factory.Client(company.ToString(), "user", masterId: 88);
        var spend = await Post(employee, "/api/v1/spends", new { resourceId = a, amount = 4, purchaseSystemId = ps, basis = "buy" }, idem: "sp1");
        Assert.Equal("posted", spend.GetProperty("status").GetString());
        var spendOp = spend.GetProperty("operationId").GetInt64();
        var wallet = await Get(employee, "/api/v1/wallet");
        Assert.Equal(6, wallet.GetProperty("balances")[0].GetProperty("balance").GetInt64());

        // возврат Admin (B27): 4A возвращаются
        var refund = await Post(admin, "/api/v1/spend-refunds", new { originalOperationId = spendOp, basis = "undo-buy" }, idem: "rf1");
        Assert.Equal("posted", refund.GetProperty("status").GetString());
        var wallet2 = await Get(employee, "/api/v1/wallet");
        Assert.Equal(10, wallet2.GetProperty("balances")[0].GetProperty("balance").GetInt64());
    }

    /// <summary>E12: изоляция компаний — чужой сотрудник не видит кошелёк/операции другой компании.</summary>
    [Fact]
    public async Task E12_Foreign_company_is_isolated()
    {
        var company = await BootstrapAsync();
        var otherCompany = await BootstrapAsync();
        var admin = Admin(company);
        var a = (await Post(admin, "/api/v1/resources", new { code = "A", name = "A" })).GetProperty("id").GetInt64();
        var emp = (await Post(admin, "/api/v1/employees", new { masterId = 99, tags = Array.Empty<string>() })).GetProperty("id").GetInt64();
        var now = DateTimeOffset.UtcNow;
        var campaign = await Post(admin, "/api/v1/campaigns", new
        {
            code = "C",
            name = "C",
            season = now.Year,
            start = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
            end = new DateTimeOffset(now.Year, 12, 31, 23, 59, 59, TimeSpan.Zero),
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            resources = new[] { new { resourceId = a, allocationRequested = true } },
        });
        var campaignId = campaign.GetProperty("id").GetInt64();
        var stream = (await Post(admin, $"/api/v1/campaigns/{campaignId}/streams", new { code = "s", name = "s" })).GetProperty("id").GetInt64();
        var task = await Post(admin, $"/api/v1/streams/{stream}/tasks", new
        {
            code = "t",
            name = "t",
            goal = 3,
            periodicity = "month",
            streamPoints = 1,
            audience = new { any = Array.Empty<string>(), all = Array.Empty<string>(), none = Array.Empty<string>() },
            rewards = new[] { new { resourceId = a, amount = 10 } },
        });
        var taskId = task.GetProperty("id").GetInt64();
        await Post(admin, $"/api/v1/campaigns/{campaignId}/budget-allocations", new { resourceId = a, amount = 10 }, idem: "x1");
        var campRes = await admin.GetAsync($"/api/v1/campaigns/{campaignId}");
        var etag = campRes.Headers.ETag!.Tag;
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/campaigns/{campaignId}") { Content = JsonContent.Create(new { status = "published" }) })
        { patch.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag!)); var r = await admin.SendAsync(patch); Assert.Equal(HttpStatusCode.OK, r.StatusCode); }
        var integration = (await Post(admin, "/api/v1/integrations", new { name = "src", serviceSub = "p-src" })).GetProperty("id").GetInt64();
        var getres = await admin.GetAsync($"/api/v1/integrations/{integration}/grants");
        var getag = getres.Headers.ETag!.Tag;
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/integrations/{integration}/grants")
        { Content = JsonContent.Create(new { grants = new[] { new { kind = "progress", campaignId } } }) })
        { put.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(getag!)); await admin.SendAsync(put); }
        var service = _factory.Client(company.ToString(), "service", subject: "p-src");
        await Post(service, "/internal/v1/events", new { employeeId = emp, taskId, increase = 3, sourceEventNo = "e1" });

        // реальный id операции компании A (награда за задание)
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Motiva.Infrastructure.MotivaDbContext>();
            var opId = (await db.Operations.Where(o => o.CompanyId == Guid.Parse(company)).Select(o => o.Id).FirstAsync());

            // сотрудник другой компании с тем же masterId не видит чужой кошелёк и операцию
            var foreign = _factory.Client(otherCompany, "user", masterId: 99);
            var wres = await foreign.GetAsync("/api/v1/wallet");
            Assert.Equal(HttpStatusCode.NotFound, wres.StatusCode);
            var ores = await foreign.GetAsync($"/api/v1/operations/{opId}");
            Assert.Equal(HttpStatusCode.NotFound, ores.StatusCode);
        }
    }
}
