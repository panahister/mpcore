using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MPCore.Application.Modules;
using MPCore.Backend.Api.Hosting;
using MPCore.Backend.Infrastructure.Persistence;
using MPCore.Domain.Events;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;
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

    private static readonly string[] ReadingVerbs =
        ["Get", "Find", "List", "Read", "Count", "Exists", "Is", "Has", "Search", "Query", "Load", "Lookup", "TryGet", "TryFind"];

    private static IReadOnlyList<Assembly> Modules =>
        [.. HandlerAssemblies.All.Where(static assembly => assembly.GetName().Name!.Contains(".Modules.", StringComparison.Ordinal))];

    /// <summary>The Contracts projects of the modules, as built next to these tests.</summary>
    private static IReadOnlyList<Assembly> Contracts =>
        [.. Directory.GetFiles(AppContext.BaseDirectory, "*.Modules.*.Contracts.dll").Select(static path => Assembly.LoadFrom(path))];

    /// <summary>The model of the one context, built without a database: a model needs no connection.</summary>
    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        PostgreSqlDbContextOptions.Apply(options, "Host=model.invalid;Database=model-only");
        using var context = new AppDbContext(options.Options, TimeProvider.System, new NullAggregateEventSink());
        return context.Model;
    }

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

    /// <summary>
    /// A module maps its tables into one schema of its own, and nothing else maps into it, so the module's
    /// tables can move to a database of their own without being taken apart. Tables of the host and of MP Core
    /// keep their own schemas, which no module may use either.
    /// </summary>
    [Fact]
    public void Each_module_maps_to_its_own_schema()
    {
        var model = Model();
        var tables = model.GetEntityTypes().Where(static entity => entity.GetTableName() is not null).ToList();
        var failures = new List<string>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal) { [SchemaOf(null)] = "the host" };
        foreach (var entity in tables.Where(entity => !Modules.Contains(entity.ClrType.Assembly)))
        {
            owners.TryAdd(SchemaOf(entity), "the host");
        }

        foreach (var module in Modules)
        {
            var name = module.GetName().Name!;
            var schemas = tables.Where(entity => entity.ClrType.Assembly == module).Select(SchemaOf).Distinct(StringComparer.Ordinal).ToList();
            if (schemas.Count > 1)
            {
                failures.Add($"{name} maps to {string.Join(" and ", schemas)}");
            }

            foreach (var schema in schemas)
            {
                if (!owners.TryAdd(schema, name))
                {
                    failures.Add($"{name} maps into {schema}, the schema of {owners[schema]}");
                }
            }
        }

        Assert.True(failures.Count == 0, "Modules that do not own exactly one schema: " + string.Join("; ", failures));
    }

    /// <summary>
    /// A foreign key across schemas ties two modules' tables together in the database, where no project
    /// reference shows it; the day one of them becomes a service, the key has to be cut. It is read from the
    /// Entity Framework model, which is what the migrations create.
    /// </summary>
    [Fact]
    public void No_foreign_key_crosses_a_schema()
    {
        var failures = Model().GetEntityTypes()
            .SelectMany(static entity => entity.GetForeignKeys())
            .Where(static key => !string.Equals(key.DeclaringEntityType.GetSchema(), key.PrincipalEntityType.GetSchema(), StringComparison.Ordinal))
            .Select(static key => $"{key.DeclaringEntityType.ClrType.Name} ({key.DeclaringEntityType.GetSchema()}) -> {key.PrincipalEntityType.ClrType.Name} ({key.PrincipalEntityType.GetSchema()})")
            .ToList();

        Assert.True(failures.Count == 0, "Foreign keys that cross a schema: " + string.Join("; ", failures));
    }

    /// <summary>
    /// Between modules the default is a message; a call that reads is always fine. An interface in a Contracts
    /// project that writes is a deliberate exception for two modules that must change together and stay in one
    /// deployment, and declares that reason with <see cref="CrossModuleWriteAttribute"/>.
    /// </summary>
    [Fact]
    public void A_contracts_interface_that_writes_declares_its_reason()
    {
        var failures = Contracts
            .SelectMany(static contracts => contracts.GetExportedTypes())
            .Where(static type => type.IsInterface && type.GetMethods().Any(static method => !Reads(method)))
            .Where(static type => type.GetCustomAttribute<CrossModuleWriteAttribute>() is null)
            .Select(static type => type.FullName!)
            .ToList();

        Assert.True(failures.Count == 0, "Contracts interfaces that write without [CrossModuleWrite(reason)]: " + string.Join(", ", failures));
    }

    /// <summary>
    /// A handler changes its own module's aggregates only: a repository it takes, in its method or in its
    /// class's constructor, is its own module's. A repository another module publishes in its Contracts
    /// project is that module's too, and so is an <c>IRepository&lt;T, TId&gt;</c> of another module's aggregate.
    /// </summary>
    [Fact]
    public void A_handler_takes_only_its_own_modules_repositories()
    {
        var failures = new List<string>();
        foreach (var module in Modules)
        {
            foreach (var handler in module.GetTypes())
            {
                var methods = handler.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(static method => method.Name is "Handle" or "HandleAsync" or "Consume" or "ConsumeAsync")
                    .ToList();
                if (methods.Count == 0)
                {
                    continue;
                }

                var dependencies = methods.SelectMany(static method => method.GetParameters())
                    .Concat(handler.GetConstructors().SelectMany(static constructor => constructor.GetParameters()))
                    .Select(static parameter => parameter.ParameterType)
                    .Distinct();
                foreach (var dependency in dependencies.Where(IsRepository))
                {
                    var owner = ModuleOf(dependency);
                    if (owner is not null && owner != module.GetName().Name)
                    {
                        failures.Add($"{handler.FullName} takes {dependency.Name} of {owner}");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, "Handlers that take another module's repository: " + string.Join("; ", failures));
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

    private static string SchemaOf(IEntityType? entity) => entity?.GetSchema() ?? "the default schema";

    /// <summary>
    /// A method reads when it is a property getter, or returns a value and its name starts with a reading verb
    /// (<c>Get</c>, <c>Find</c>, <c>TryGet</c> ...). Anything else is taken to write, so a new verb errs
    /// towards asking for a reason.
    /// </summary>
    private static bool Reads(MethodInfo method) =>
        method.IsSpecialName
            ? !method.Name.StartsWith("set_", StringComparison.Ordinal)
            : method.ReturnType != typeof(void) && method.ReturnType != typeof(Task) && method.ReturnType != typeof(ValueTask) &&
              ReadingVerbs.Any(verb => method.Name.StartsWith(verb, StringComparison.Ordinal) &&
                                       (method.Name.Length == verb.Length || !char.IsLower(method.Name[verb.Length])));

    private static bool IsRepository(Type type) =>
        type.Name.EndsWith("Repository", StringComparison.Ordinal) || AggregateOf(type) is not null;

    /// <summary>The aggregate of an <c>IRepository&lt;T, TId&gt;</c>, if the type is or implements one.</summary>
    private static Type? AggregateOf(Type type) =>
        type.GetInterfaces().Append(type)
            .FirstOrDefault(static contract => contract.IsGenericType && contract.GetGenericTypeDefinition().FullName == "MPCore.Persistence.Abstractions.IRepository`2")
            ?.GetGenericArguments()[0];

    /// <summary>The module a type belongs to: its own project, or the module whose Contracts project declares it.</summary>
    private static string? ModuleOf(Type type)
    {
        var name = (AggregateOf(type) ?? type).Assembly.GetName().Name!;
        if (name.EndsWith(".Contracts", StringComparison.Ordinal))
        {
            name = name[..^".Contracts".Length];
        }

        return Modules.Any(module => module.GetName().Name == name) ? name : null;
    }
}
