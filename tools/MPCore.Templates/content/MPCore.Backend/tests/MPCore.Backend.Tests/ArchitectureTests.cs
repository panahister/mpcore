using System.Reflection;
using NetArchTest.Rules;

namespace MPCore.Backend.Tests;

/// <summary>
/// The layers of this service, held by tests. The direction of dependencies is the Dependency Rule of
/// Robert C. Martin's "Clean Architecture" (2017), and the ports are Alistair Cockburn's "Hexagonal
/// Architecture" (2005): the domain and the application know ports, never a provider. The project
/// references already forbid most of it; these tests guard what the compiler cannot see.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(MPCore.Backend.Domain.AssemblyReference).Assembly;
    private static readonly Assembly Application = typeof(MPCore.Backend.Application.AssemblyReference).Assembly;

    private static readonly string[] Providers =
        ["Microsoft.EntityFrameworkCore", "Npgsql", "Wolverine", "Confluent.Kafka", "Microsoft.AspNetCore", "Grpc"];

    [Fact]
    public void The_domain_and_the_application_name_no_provider()
    {
        foreach (var assembly in new[] { Domain, Application })
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(Providers).GetResult();
            Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
        }
    }

    [Fact]
    public void The_domain_knows_nothing_of_the_layers_around_it()
    {
        var result = Types.InAssembly(Domain).ShouldNot()
            .HaveDependencyOnAny("MPCore.Backend.Application", "MPCore.Backend.Infrastructure", "MPCore.Backend.Api").GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void A_query_handler_takes_no_unit_of_work_and_publishes_nothing()
    {
        var violations = QueryRules.Violations(Application).ToList();

        Assert.True(violations.Count == 0, "Query handlers that could change state: " + string.Join(", ", violations));
    }

    [Fact]
    public void The_application_knows_neither_the_adapters_nor_the_host()
    {
        var result = Types.InAssembly(Application).ShouldNot()
            .HaveDependencyOnAny("MPCore.Backend.Infrastructure", "MPCore.Backend.Api").GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
