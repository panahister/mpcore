using System.Reflection;
using MPCore.Backend.Api.Hosting;
using NetArchTest.Rules;

namespace MPCore.Backend.Tests;

/// <summary>
/// The rules of this modular monolith's modules, held by tests. A module is one project with its layers as
/// folders (MP Core ADR-012): the compiler keeps the modules apart, and these tests keep apart what the
/// compiler cannot see, the layers inside one module. This is Simon Brown's "package by component"
/// ("The Missing Chapter", in Robert C. Martin's "Clean Architecture", 2017) with the Dependency Rule of the
/// same book inside each component. A module is checked once it is listed in <see cref="HandlerAssemblies"/>.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class ModuleRulesTests
{
    private static readonly string[] Providers =
        ["Microsoft.EntityFrameworkCore", "Npgsql", "Wolverine", "Confluent.Kafka", "RabbitMQ", "Microsoft.AspNetCore", "Grpc"];

    private static IReadOnlyList<Assembly> Modules =>
        [.. HandlerAssemblies.All.Where(static assembly => assembly.GetName().Name!.Contains(".Modules.", StringComparison.Ordinal))];

    [Fact]
    public void Each_modules_domain_knows_neither_its_other_layers_nor_a_provider()
    {
        var failures = new List<string>();
        foreach (var module in Modules)
        {
            var root = module.GetName().Name!;
            var result = Types.InAssembly(module).That().ResideInNamespace(root + ".Domain").ShouldNot()
                .HaveDependencyOnAny([root + ".Application", root + ".Infrastructure", .. Providers]).GetResult();
            failures.AddRange(result.FailingTypeNames ?? []);
        }

        Assert.True(failures.Count == 0, "Domain types that depend on another layer or on a provider: " + string.Join(", ", failures));
    }

    [Fact]
    public void Each_modules_application_knows_neither_its_infrastructure_nor_a_provider()
    {
        var failures = new List<string>();
        foreach (var module in Modules)
        {
            var root = module.GetName().Name!;
            var result = Types.InAssembly(module).That().ResideInNamespace(root + ".Application").ShouldNot()
                .HaveDependencyOnAny([root + ".Infrastructure", .. Providers]).GetResult();
            failures.AddRange(result.FailingTypeNames ?? []);
        }

        Assert.True(failures.Count == 0, "Application types that depend on the infrastructure or on a provider: " + string.Join(", ", failures));
    }

    [Fact]
    public void A_query_handler_takes_no_unit_of_work_and_publishes_nothing()
    {
        var violations = Modules.SelectMany(QueryRules.Violations).ToList();

        Assert.True(violations.Count == 0, "Query handlers that could change state: " + string.Join(", ", violations));
    }

    [Fact]
    public void No_module_references_another_modules_main_project()
    {
        var names = Modules.Select(static module => module.GetName().Name!).ToHashSet(StringComparer.Ordinal);
        var failures = Modules
            .SelectMany(module => module.GetReferencedAssemblies()
                .Where(reference => reference.Name != module.GetName().Name && names.Contains(reference.Name!))
                .Select(reference => $"{module.GetName().Name} -> {reference.Name}"))
            .ToList();

        Assert.True(failures.Count == 0, "Modules that reference another module's main project instead of its Contracts: " + string.Join(", ", failures));
    }
}
