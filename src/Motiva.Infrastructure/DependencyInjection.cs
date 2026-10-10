using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Motiva.Application.Ports;
using Npgsql;
using StackExchange.Redis;

namespace Motiva.Infrastructure;

public static class InfrastructureDependencyInjection
{
    public static IServiceCollection AddMotivaInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var conn = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres not configured");

        // NpgsqlDataSource с поддержкой динамического JSON (jsonb-поля Audience/Tags/Details)
        var dataSource = new NpgsqlDataSourceBuilder(conn).EnableDynamicJson().Build();
        services.AddSingleton(dataSource);

        services.AddPooledDbContextFactory<MotivaDbContext>(o => o.UseNpgsql(dataSource, x => x.MigrationsAssembly("Motiva.Infrastructure")));
        services.AddScoped<MotivaDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<MotivaDbContext>>().CreateDbContext());
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddSingleton<ITime, SystemClock>();
        services.AddSingleton<Motiva.Domain.ICompanyCalendar>(_ =>
            new Motiva.Domain.CompanyCalendar(configuration["Company:TimeZoneId"] ?? "Europe/Tallinn"));

        // Valkey
        var redisConn = configuration.GetConnectionString("Valkey") ?? "localhost:6379";
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConn));
        services.AddSingleton<ICache, ValkeyCache>();
        services.AddOptions<RedisOptions>().Configure(o => o.ConnectionString = redisConn);

        // S3/MinIO
        services.AddSingleton<IObjectStorage>(sp =>
        {
            var s = configuration.GetSection("Storage");
            return new MinioStorage(new Microsoft.Extensions.Options.OptionsWrapper<ObjectStorageOptions>(new ObjectStorageOptions
            {
                Endpoint = s["Endpoint"] ?? "localhost:9000",
                AccessKey = s["AccessKey"] ?? "minio",
                SecretKey = s["SecretKey"] ?? "minio123",
                Bucket = s["Bucket"] ?? "motiva",
                UseSsl = bool.TryParse(s["UseSsl"], out var u) && u,
            }));
        });

        services.AddScoped<IStatementGenerator, StatementGeneratorService>();

        return services;
    }
}
