using System.Reflection;
using Wolverine;

namespace MPCore.Messaging.Wolverine;

/// <summary>
/// Explicit handler discovery. A host names the assemblies whose message handlers it owns; MP Core
/// never scans an assembly that was not named, and registers no catch-all route or handler policy.
/// </summary>
public static class WolverineDiscoveryExtensions
{
    /// <summary>
    /// Includes the assemblies whose message handlers this host owns. Each layer or module exposes its
    /// own <c>AssemblyReference</c>, so the list reads as the set of owners rather than a scan.
    /// </summary>
    /// <param name="options">The Wolverine options being configured.</param>
    /// <param name="assemblies">At least one assembly. Duplicates are ignored; a null entry is an error.</param>
    /// <returns>The same options, for chaining.</returns>
    /// <remarks>
    /// Wolverine also discovers handlers in its application assembly: the host project itself, which the
    /// team owns and reviews. <c>UseMPCoreWolverine</c> sets it from
    /// <see cref="WolverineFoundationOptions.ApplicationAssembly"/> (or the entry assembly), because Wolverine's
    /// own inference would otherwise land on MP Core. Every other assembly must appear here.
    /// </remarks>
    public static WolverineOptions DiscoverHandlersIn(this WolverineOptions options, params Assembly[] assemblies) =>
        options.DiscoverHandlersIn((IEnumerable<Assembly>)(assemblies ?? throw new ArgumentNullException(nameof(assemblies))));

    /// <summary>
    /// Includes the assemblies whose message handlers this host owns.
    /// </summary>
    /// <param name="options">The Wolverine options being configured.</param>
    /// <param name="assemblies">At least one assembly. Duplicates are ignored; a null entry is an error.</param>
    /// <returns>The same options, for chaining.</returns>
    public static WolverineOptions DiscoverHandlersIn(this WolverineOptions options, IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assemblies);

        var included = new HashSet<Assembly>();
        foreach (var assembly in assemblies)
        {
            if (assembly is null)
            {
                throw new ArgumentException(
                    "One of the assemblies is null. Name each owner explicitly, for example Application.AssemblyReference.Assembly.",
                    nameof(assemblies));
            }

            if (included.Add(assembly))
            {
                options.Discovery.IncludeAssembly(assembly);
            }
        }

        if (included.Count == 0)
        {
            // Silently discovering nothing is the failure this method exists to prevent: the host would
            // start, accept a command and answer "no handler" only at the first real request.
            throw new ArgumentException(
                "Name at least one assembly whose message handlers this host owns, for example Application.AssemblyReference.Assembly.",
                nameof(assemblies));
        }

        return options;
    }
}
