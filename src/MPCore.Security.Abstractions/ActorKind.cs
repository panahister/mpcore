namespace MPCore.Security;

/// <summary>
/// Identifies the kind of caller behind the current request.
/// </summary>
public enum ActorKind
{
    /// <summary>No validated credential was presented.</summary>
    Anonymous = 0,

    /// <summary>An end user authenticated through the identity provider.</summary>
    User = 1,

    /// <summary>A machine client authenticated with its own credential.</summary>
    Service = 2,

    /// <summary>
    /// The system itself: a scheduled job, a migration, a background process. Never produced from a
    /// token; entered explicitly through <see cref="SystemActorScope"/> by code that owns the work.
    /// </summary>
    System = 3
}
