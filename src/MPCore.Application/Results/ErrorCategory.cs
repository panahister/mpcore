namespace MPCore.Application.Results;

/// <summary>
/// The transport-neutral classification of a failure. Every transport adapter maps this closed set
/// onto its own wire vocabulary, so the two renderings can never disagree about meaning.
/// </summary>
public enum ErrorCategory
{
    /// <summary>The failure could not be classified. No detail is disclosed to the caller.</summary>
    Unknown = 0,

    /// <summary>The request was syntactically or structurally invalid.</summary>
    Validation = 1,

    /// <summary>The addressed resource does not exist.</summary>
    NotFound = 2,

    /// <summary>The resource the caller tried to create already exists.</summary>
    AlreadyExists = 3,

    /// <summary>A domain invariant refused an otherwise well-formed request.</summary>
    BusinessRule = 4,

    /// <summary>A required domain precondition was not satisfied.</summary>
    Precondition = 5,

    /// <summary>The operation conflicts with the current state of the resource.</summary>
    Conflict = 6,

    /// <summary>The operation lost an optimistic-concurrency check.</summary>
    Concurrency = 7,

    /// <summary>Authentication was missing, malformed, expired or otherwise invalid.</summary>
    Unauthenticated = 8,

    /// <summary>The caller was authenticated but lacked sufficient authority.</summary>
    Forbidden = 9,

    /// <summary>The caller exceeded a request-rate limit.</summary>
    RateLimit = 10,

    /// <summary>The caller exhausted an allocated quota.</summary>
    Quota = 11,

    /// <summary>A dependency the operation needs is temporarily unavailable.</summary>
    DependencyUnavailable = 12,

    /// <summary>The operation exceeded its deadline.</summary>
    Deadline = 13,

    /// <summary>The operation was cancelled before it completed.</summary>
    Cancelled = 14
}
