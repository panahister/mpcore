namespace MPCore.Security;

/// <summary>
/// Exposes the actor behind the current logical operation without leaking any transport,
/// framework or identity-provider type into application code.
/// </summary>
public interface ICurrentActorAccessor
{
    /// <summary>
    /// Gets the current actor. Implementations never return <see langword="null"/> and return
    /// <see cref="CurrentActor.Anonymous"/> when no validated credential is available.
    /// </summary>
    CurrentActor Current { get; }
}
