using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Motiva.Api;
using Motiva.Infrastructure;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace Motiva.Tests.Integration;

/// <summary>HTTP/contract и functional integration: реальные PG17/Valkey/MinIO, подписанные JWT (T08/T03).</summary>
public sealed class MotivaFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("valkey/valkey:9-alpine").Build();
    private readonly MinioContainer _minio = new MinioBuilder()
        .WithImage("minio/minio:latest").Build();

    public string SigningKeyB64 { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_pg.StartAsync(), _redis.StartAsync(), _minio.StartAsync());
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MotivaDbContext>();
        await db.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(_pg.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask(), _minio.DisposeAsync().AsTask());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = _pg.GetConnectionString(),
            ["ConnectionStrings:Valkey"] = _redis.GetConnectionString(),
            ["Storage:Endpoint"] = $"localhost:{_minio.GetMappedPublicPort(9000)}",
            ["Storage:AccessKey"] = _minio.GetAccessKey(),
            ["Storage:SecretKey"] = _minio.GetSecretKey(),
            ["Storage:Bucket"] = "motiva",
            ["Storage:UseSsl"] = "false",
            ["Auth:Issuer"] = "motiva-benchmark",
            ["Auth:Audience"] = "motiva-api",
            ["Auth:SigningKeyBase64"] = SigningKeyB64,
            ["Company:TimeZoneId"] = "Europe/Tallinn",
        };
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }

    /// <summary>Выпустить тестовый подписанный JWT (разрешено в тестовом коде, T03).</summary>
    public string Token(string companyId, string actorType, int? masterId = null, bool admin = false, string? subject = null)
    {
        var key = new SymmetricSecurityKey(Convert.FromBase64String(SigningKeyB64));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTimeOffset.UtcNow;
        var claims = new List<System.Security.Claims.Claim>
        {
            new("companyId", companyId),
            new("actorType", actorType),
            new("sub", subject ?? (actorType == "user" ? $"employee-{masterId}" : "svc")),
        };
        if (actorType == "user" && masterId is not null)
        {
            claims.Add(new("masterId", masterId.Value.ToString()));
        }

        if (actorType == "user")
        {
            claims.Add(new("role", "Employee"));
        }

        if (admin)
        {
            claims.Add(new("role", "Admin"));
        }

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: "motiva-benchmark", audience: "motiva-api",
            claims: claims, notBefore: now.UtcDateTime, expires: now.AddHours(1).UtcDateTime, signingCredentials: creds);
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    public HttpClient Client(string companyId, string actorType, int? masterId = null, bool admin = false, string? subject = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(companyId, actorType, masterId, admin, subject));
        return client;
    }

    public const string Company = "11111111-1111-4111-8111-111111111111";
}

/// <summary>Общие помощники для интеграционных тестов.</summary>
public static class TestData
{
    public static Guid NewCompany() => Guid.NewGuid();

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
