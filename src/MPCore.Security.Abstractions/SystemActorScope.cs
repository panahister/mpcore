namespace MPCore.Security;

/// <summary>
/// Marks a stretch of code as executed by the system itself — a scheduled job, a migration, a
/// background consumer — so that audit, logging and authorization see a named system actor instead
/// of "anonymous". Flows with the async context and ends with the scope.
/// </summary>
public static class SystemActorScope
{
    private static readonly AsyncLocal<CurrentActor?> Ambient = new();

    /// <summary>The system actor of the innermost open scope, or null outside any scope.</summary>
    public static CurrentActor? Current => Ambient.Value;

    /// <summary>Opens a scope for the named process. Dispose to leave it.</summary>
    public static IDisposable Enter(string processName)
    {
        var previous = Ambient.Value;
        Ambient.Value = CurrentActor.ForSystem(processName);
        return new Exit(previous);
    }

    private sealed class Exit(CurrentActor? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>
/// Actor accessor for hosts without an HTTP request — workers, consoles, tests. Returns the open
/// <see cref="SystemActorScope"/> actor, otherwise anonymous.
/// </summary>
public sealed class AmbientCurrentActorAccessor : ICurrentActorAccessor
{
    /// <inheritdoc />
    public CurrentActor Current => SystemActorScope.Current ?? CurrentActor.Anonymous;
}
