namespace MPCore.Backend.Api.Hosting;

/// <summary>
/// Registration for the developer-facing description surfaces: the OpenAPI document and its UI on
/// REST, and gRPC server reflection.
/// </summary>
/// <remarks>
/// Both describe the whole API surface to anyone who can reach them, which is useful while building
/// and an inventory for an attacker in production. They are therefore off unless the environment is
/// Development, and the explicit settings can only be read as an opt-in — a missing or malformed
/// value leaves them off rather than on.
/// </remarks>
public static class DeveloperEndpoints
{
    /// <summary>
    /// True when a description surface may be exposed: Development by default, overridable per
    /// setting for a controlled non-production environment such as an internal test host.
    /// </summary>
    public static bool IsDescriptionSurfaceEnabled(IConfiguration configuration, IHostEnvironment environment, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // GetValue<bool?> returns null for both "absent" and "unparseable", so anything that is not an
        // explicit true falls back to the environment. There is no path where a typo enables it.
        var configured = configuration.GetValue<bool?>(key);
        return configured ?? environment.IsDevelopment();
    }
}
