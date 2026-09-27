using System.Reflection;
using MPCore.Security.AspNetCore;

namespace MPCore.Security.Tests;

/// <summary>
/// Regression cover for the 0.2.0-alpha.2 consumer defect in which a packaged template generated
/// <c>AddMPCoreAuthorization(options =&gt; options.AllowAnonymousHealthEndpoints = ...)</c> and the
/// generated host failed to compile with CS1061.
/// </summary>
/// <remarks>
/// The correct resolution is the one asserted here: the property stays absent. MP Core does not map
/// the health probe endpoints, so it cannot enforce anonymous access to them, and a public option
/// that no MP Core code reads would be a misleading security control. Adding the property back to
/// satisfy a stale template would ship exactly that lie, so this test fails if anyone does.
/// </remarks>
public sealed class AuthorizationSurfaceRegressionTests
{
    [Fact]
    public void The_authorization_options_do_not_expose_anonymous_health_access()
    {
        var property = typeof(MPCoreAuthorizationOptions).GetProperty(
            "AllowAnonymousHealthEndpoints",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        Assert.Null(property);
    }

    [Fact]
    public void The_authorization_options_expose_exactly_the_supported_surface()
    {
        var names = typeof(MPCoreAuthorizationOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "ApplyAuthenticatedFallbackPolicy" }, names);
    }

    [Fact]
    public void Authorization_registers_without_any_configuration_callback()
    {
        // The template calls the parameterless overload. It must remain callable, and protect by
        // default must remain the unconfigured behavior.
        var method = typeof(SecurityRegistrationExtensions)
            .GetMethod(nameof(SecurityRegistrationExtensions.AddMPCoreAuthorization));

        Assert.NotNull(method);
        var configure = Assert.Single(method!.GetParameters(), p => p.Name == "configure");
        Assert.True(configure.IsOptional);
        Assert.True(new MPCoreAuthorizationOptions().ApplyAuthenticatedFallbackPolicy);
    }
}
