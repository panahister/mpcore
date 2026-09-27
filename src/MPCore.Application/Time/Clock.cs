namespace MPCore.Application.Time;

/// <summary>
/// The application-layer time port. Application code never reads the ambient system clock directly,
/// so time remains substitutable in tests.
/// </summary>
public interface IClock
{
    /// <summary>Gets the current instant in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>The default <see cref="IClock"/> backed by a <see cref="TimeProvider"/>.</summary>
/// <param name="timeProvider">The underlying time provider.</param>
public sealed class SystemClock(TimeProvider timeProvider) : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}
