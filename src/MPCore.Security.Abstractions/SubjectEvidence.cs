namespace MPCore.Security;

/// <summary>
/// The person on whose behalf a service calls, proved by that person's own token, which the calling service
/// carried beside its own identity and this host validated. A bounded record: never the token.
/// </summary>
/// <remarks>
/// The caller of the request is still the service (<see cref="CurrentActor"/>); this record says for whom it
/// calls. A handler that decides on the person's own permission reads it, and records both.
/// </remarks>
public sealed record SubjectEvidence
{
    /// <summary>Creates the record; every member is required and at most 256 characters.</summary>
    /// <param name="subjectId">The person's stable subject identifier (<c>sub</c>).</param>
    /// <param name="sessionId">The person's session at the identity provider (<c>sid</c>).</param>
    /// <param name="issuer">The validated issuer of the person's token.</param>
    /// <param name="authenticatedAt">When the person authenticated (<c>auth_time</c>).</param>
    public SubjectEvidence(string subjectId, string sessionId, string issuer, DateTimeOffset authenticatedAt)
    {
        SubjectId = Bounded(subjectId, nameof(subjectId));
        SessionId = Bounded(sessionId, nameof(sessionId));
        Issuer = Bounded(issuer, nameof(issuer));
        AuthenticatedAt = authenticatedAt;
    }

    /// <summary>Gets the person's stable subject identifier.</summary>
    public string SubjectId { get; }

    /// <summary>Gets the person's session at the identity provider.</summary>
    public string SessionId { get; }

    /// <summary>Gets the validated issuer of the person's token.</summary>
    public string Issuer { get; }

    /// <summary>Gets when the person authenticated.</summary>
    public DateTimeOffset AuthenticatedAt { get; }

    /// <summary>A bounded description that leaves the session out.</summary>
    public override string ToString() => $"SubjectEvidence {{ SubjectId = {SubjectId}, Issuer = {Issuer} }}";

    private static string Bounded(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > CurrentActor.MaximumMemberLength)
        {
            throw new ArgumentException($"{name} cannot exceed {CurrentActor.MaximumMemberLength} characters.", name);
        }

        return value;
    }
}

/// <summary>The evidence of the person behind the current call, when the host validated one.</summary>
public interface ISubjectEvidenceAccessor
{
    /// <summary>Gets the validated evidence; null when the call carries none, or carries one that was refused.</summary>
    SubjectEvidence? Current { get; }
}
