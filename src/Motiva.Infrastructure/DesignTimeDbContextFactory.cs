using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Motiva.Infrastructure;

/// <summary>Design-time фабрика для `dotnet ef` (T01).</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MotivaDbContext>
{
    public MotivaDbContext CreateDbContext(string[] args)
    {
        var connectionString = System.Environment.GetEnvironmentVariable("MOTIVA_PG")
            ?? "Host=localhost;Port=5432;Database=motiva;Username=motiva;Password=motiva";
        var builder = new DbContextOptionsBuilder<MotivaDbContext>();
        builder.UseNpgsql(connectionString, x => x.MigrationsAssembly("Motiva.Infrastructure"));
        return new MotivaDbContext(builder.Options);
    }
}
