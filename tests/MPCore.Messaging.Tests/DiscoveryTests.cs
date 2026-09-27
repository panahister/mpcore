using System.Reflection;
using MPCore.Messaging.Wolverine;
using Wolverine;

namespace MPCore.Messaging.Tests;

public sealed class DiscoveryTests
{
    private static WolverineOptions Options() => new();

    [Fact]
    public void Naming_an_owner_assembly_is_accepted_and_chainable()
    {
        var options = Options();
        Assert.Same(options, options.DiscoverHandlersIn(typeof(DiscoveryTests).Assembly));
    }

    [Fact]
    public void The_same_assembly_named_twice_is_included_once()
    {
        // Wolverine would throw on a duplicate include; the point is that a host may list the same
        // owner twice (two modules in one assembly, say) without configuring itself into a failure.
        var options = Options();
        var assembly = typeof(DiscoveryTests).Assembly;
        Assert.Same(options, options.DiscoverHandlersIn(assembly, assembly));
        Assert.Same(options, options.DiscoverHandlersIn([assembly, typeof(WolverineDiscoveryExtensions).Assembly, assembly]));
    }

    [Fact]
    public void Discovering_nothing_is_refused_rather_than_starting_a_host_with_no_handlers()
    {
        var empty = Assert.Throws<ArgumentException>(() => Options().DiscoverHandlersIn());
        Assert.Contains("at least one assembly", empty.Message, StringComparison.Ordinal);
        Assert.Contains("at least one assembly", Assert.Throws<ArgumentException>(() => Options().DiscoverHandlersIn(Array.Empty<Assembly>().AsEnumerable())).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_owner_is_an_error_not_a_silent_skip()
    {
        Assert.Throws<ArgumentException>(() => Options().DiscoverHandlersIn([typeof(DiscoveryTests).Assembly, null!]));
        Assert.Throws<ArgumentNullException>(() => Options().DiscoverHandlersIn((Assembly[])null!));
        Assert.Throws<ArgumentNullException>(() => Options().DiscoverHandlersIn((IEnumerable<Assembly>)null!));
    }
}
