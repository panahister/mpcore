using System.Reflection;
using MPCore.Audit.EntityFrameworkCore;
using Xunit;

namespace MPCore.Audit.Tests;

/// <summary>
/// ADR-011 §7: Wolverine generates handler code without service location, so every type in a handler's
/// dependency graph must be public and must not take <see cref="IServiceProvider"/> or
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"/>. A handler that declares
/// <see cref="IBusinessAuditRecorder"/> pulls in every type below.
/// </summary>
/// <remarks>
/// Found when the first use case ran a generated host: every handler that recorded a business action failed
/// with Wolverine's InvalidServiceLocationException, because the audit sink took IServiceScopeFactory.
/// </remarks>
public sealed class HandlerGraphConstraintTests
{
    public static TheoryData<Type> TypesInTheRecorderGraph() =>
    [
        typeof(BusinessAuditRecorder),
        typeof(EntityFrameworkAuditSink<AuditTestContext>),
    ];

    [Theory]
    [MemberData(nameof(TypesInTheRecorderGraph))]
    public void A_type_in_a_handler_graph_is_public_and_takes_no_service_provider(Type type)
    {
        Assert.True(type.IsPublic, $"{type.Name} must be public for Wolverine's generated code.");
        var parameters = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(static c => c.GetParameters())
            .Select(static p => p.ParameterType)
            .ToArray();
        Assert.DoesNotContain(typeof(IServiceProvider), parameters);
        Assert.DoesNotContain(typeof(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory), parameters);
    }
}
