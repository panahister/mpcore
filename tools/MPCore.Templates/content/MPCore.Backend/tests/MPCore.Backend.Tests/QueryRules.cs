using System.Reflection;

namespace MPCore.Backend.Tests;

/// <summary>
/// A query only reads (MP Core ADR-012, section 2; Bertrand Meyer's command-query separation): its handler
/// declares no unit of work and publishes nothing, so an HTTP <c>GET</c>, which RFC 9110 requires to be safe,
/// can never change anything. A handler is a <c>Handle</c> or <c>Consume</c> method whose first parameter is
/// an <c>IQuery&lt;T&gt;</c>; what it takes in that method or in its class's constructor is checked.
/// </summary>
internal static class QueryRules
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
    {
        "MPCore.Persistence.Abstractions.IUnitOfWork",
        "MPCore.Messaging.Abstractions.IMessagePublisher",
        "Wolverine.IMessageBus",
        "Wolverine.IMessageContext",
    };

    public static IEnumerable<string> Violations(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in methods.Where(static method => method.Name is "Handle" or "HandleAsync" or "Consume" or "ConsumeAsync"))
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 0 || !IsQuery(parameters[0].ParameterType))
                {
                    continue;
                }

                var dependencies = parameters.Skip(1).Select(static parameter => parameter.ParameterType)
                    .Concat(type.GetConstructors().SelectMany(static constructor => constructor.GetParameters()).Select(static parameter => parameter.ParameterType));
                foreach (var dependency in dependencies.Where(static dependency => dependency.FullName is { } name && Forbidden.Contains(name)))
                {
                    yield return $"{type.FullName}.{method.Name} takes {dependency.Name}";
                }
            }
        }
    }

    private static bool IsQuery(Type type) =>
        type.GetInterfaces().Any(static contract =>
            contract.IsGenericType && contract.GetGenericTypeDefinition().FullName == "MPCore.Application.Messaging.IQuery`1");
}
