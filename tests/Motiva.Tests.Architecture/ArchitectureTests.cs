using System.Reflection;
using Motiva.Domain;
using Xunit;

namespace Motiva.Tests.Architecture;

/// <summary>Границы проектов (T02). Отрицательный контроль: компилируемое нарушение вызывает падение теста.</summary>
public class ArchitectureTests
{
    private static HashSet<string> Referenced(Assembly a) =>
        a.GetReferencedAssemblies().Select(r => r.Name ?? "").ToHashSet();

    [Fact]
    public void Domain_does_not_depend_on_EF_HTTP_or_infra()
    {
        var refs = Referenced(typeof(Employee).Assembly);
        foreach (var forbidden in new[] { "EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore", "Minio", "StackExchange.Redis", "Motiva.Application", "Motiva.Infrastructure" })
        {
            Assert.False(refs.Contains(forbidden), $"Domain не должен ссылаться на {forbidden}");
        }
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_implementations()
    {
        var refs = Referenced(typeof(Motiva.Application.Actor).Assembly);
        foreach (var forbidden in new[] { "Motiva.Infrastructure", "Minio", "Npgsql", "StackExchange.Redis", "Motiva.Api", "Motiva.Worker" })
        {
            Assert.False(refs.Contains(forbidden), $"Application не должен ссылаться на {forbidden}");
        }
    }

    [Fact]
    public void Domain_namespace_has_no_EF_types()
    {
        // смысловой контроль: в Domain нет DbContext/DbSet
        var types = typeof(Employee).Assembly.GetTypes();
        Assert.DoesNotContain(types, t => t.Name.Contains("DbContext") || t.Name.Contains("DbSet"));
    }

    [Fact]
    public void Application_namespace_has_no_database_types()
    {
        var types = typeof(Motiva.Application.Actor).Assembly.GetTypes();
        Assert.DoesNotContain(types, t => t.Namespace?.StartsWith("Microsoft.EntityFrameworkCore") == true
            || t.Name.Contains("NpgsqlConnection"));
    }
}
