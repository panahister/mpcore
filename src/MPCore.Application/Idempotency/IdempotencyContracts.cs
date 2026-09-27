using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MPCore.Application.Results;

namespace MPCore.Application.Idempotency;

/// <summary>Settings of request idempotency and of the consumer inbox.</summary>
public sealed class IdempotencyOptions
{
    /// <summary>The header, or gRPC metadata entry, that carries the key. The IETF draft names it <c>Idempotency-Key</c>.</summary>
    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>How long a used key is remembered. After that the same key starts a new operation. Defaults to 24 hours.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long the inbox remembers a processed message. Defaults to seven days.</summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How often expired keys and inbox entries are deleted. Defaults to one hour.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>What the transport found on the current request.</summary>
/// <param name="Key">The key the caller sent, or null.</param>
/// <param name="Required">Whether the endpoint requires a key.</param>
public readonly record struct IdempotencyKeyReading(string? Key, bool Required);

/// <summary>The transport's side of idempotency: where the key comes from, and how a replay is announced.</summary>
public interface IIdempotencyKeySource
{
    /// <summary>Reads the key of the current request and whether its endpoint requires one.</summary>
    IdempotencyKeyReading Read();

    /// <summary>Tells the caller that the response is a stored one (<c>Idempotency-Replayed: true</c>).</summary>
    void MarkReplayed();
}

/// <summary>Whose key it is. Two callers using the same key never see each other's responses.</summary>
public interface IIdempotencyScopeProvider
{
    /// <summary>Returns the scope of the current caller, for example <c>user:42</c>.</summary>
    string GetScope();
}

/// <summary>One remembered key.</summary>
/// <param name="Scope">Whose key it is.</param>
/// <param name="Key">The key.</param>
/// <param name="Operation">The command it was used for.</param>
/// <param name="RequestHash">The hash of the request it was used with.</param>
/// <param name="ResponseJson">The value the operation returned, as JSON; null for an operation that returns none.</param>
/// <param name="CreatedOnUtc">When the operation committed.</param>
public sealed record IdempotencyEntry(
    string Scope, string Key, string Operation, string RequestHash, string? ResponseJson, DateTimeOffset CreatedOnUtc);

/// <summary>The read side of the key store. The write happens inside the business transaction, not through this port.</summary>
public interface IIdempotencyStore
{
    /// <summary>Finds a key that has not expired.</summary>
    Task<IdempotencyEntry?> FindAsync(string scope, string key, CancellationToken cancellationToken);
}

/// <summary>The key, the caller, the command and the hash of what was asked.</summary>
/// <param name="Scope">Whose key it is.</param>
/// <param name="Operation">The command's type name.</param>
/// <param name="Key">The key.</param>
/// <param name="RequestHash">SHA-256 of the command's type and content.</param>
public sealed record IdempotencyRequest(string Scope, string Operation, string Key, string RequestHash)
{
    /// <summary>The longest key accepted.</summary>
    public const int MaximumKeyLength = 255;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Builds the request identity of a command.</summary>
    /// <param name="scope">Whose key it is.</param>
    /// <param name="message">The command.</param>
    /// <param name="key">The key.</param>
    public static IdempotencyRequest For(string scope, object message, string key)
    {
        ArgumentNullException.ThrowIfNull(message);
        var operation = message.GetType().FullName ?? message.GetType().Name;
        var content = operation + "\n" + JsonSerializer.Serialize(message, message.GetType(), Json);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return new IdempotencyRequest(scope, operation, key, hash);
    }

    /// <summary>A key is 1 to 255 visible ASCII characters; a UUID is the usual choice.</summary>
    /// <param name="key">The candidate.</param>
    public static bool IsValidKey(string? key) =>
        key is { Length: > 0 and <= MaximumKeyLength } && key.All(static character => character is > ' ' and <= '~');
}

/// <summary>
/// Runs a command at most once per key. A repeat with the same key and the same request receives the
/// stored result and the handler does not run again.
/// </summary>
/// <remarks>
/// <para>
/// The pattern is the <c>Idempotency-Key</c> header of the IETF HTTPAPI working group's draft
/// (draft-ietf-httpapi-idempotency-key-header), made common by Stripe; Brandur Leach described the
/// implementation on PostgreSQL. HTTP itself makes only GET, PUT and DELETE idempotent (RFC 9110), so a
/// client that retries a POST after a timeout cannot otherwise know whether the first attempt succeeded.
/// </para>
/// <para>
/// <b>The key commits with the change.</b> The key, the hash of the request and the returned value are
/// written by the persistence adapter in the transaction that commits the business change, so there is no
/// moment at which one exists without the other. Only an outcome that committed is remembered: a failure
/// changed nothing, and repeating it is evaluated again.
/// </para>
/// </remarks>
public interface IIdempotentExecutor
{
    /// <summary>Runs a command that returns a value.</summary>
    /// <typeparam name="TValue">The value type. It must survive a JSON round trip.</typeparam>
    /// <param name="message">The command, used for the request hash.</param>
    /// <param name="invoke">Sends the command.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<Result<TValue>> ExecuteAsync<TValue>(
        object message, Func<CancellationToken, Task<Result<TValue>>> invoke, CancellationToken cancellationToken);

    /// <summary>Runs a command that returns no value.</summary>
    /// <param name="message">The command, used for the request hash.</param>
    /// <param name="invoke">Sends the command.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<Result> ExecuteAsync(
        object message, Func<CancellationToken, Task<Result>> invoke, CancellationToken cancellationToken);
}

/// <summary>The failures request idempotency itself can produce.</summary>
public static class IdempotencyFailures
{
    /// <summary>The error domain.</summary>
    public const string Domain = "mpcore.idempotency";

    /// <summary>The endpoint requires a key and the caller sent none.</summary>
    public static FailureDescriptor KeyRequired() => Invalid("KEY_REQUIRED", "REQUIRED", "mpcore.idempotency.key_required");

    /// <summary>The key is empty, too long, or contains characters outside visible ASCII.</summary>
    public static FailureDescriptor KeyInvalid() => Invalid("KEY_INVALID", "INVALID", "mpcore.idempotency.key_invalid");

    /// <summary>The key was already used for a different request. The IETF draft answers 422.</summary>
    public static FailureDescriptor KeyReused() => new(
        new ErrorIdentity(Domain, "KEY_REUSED"),
        ErrorCategory.Precondition,
        new FailureMessageDescriptor("mpcore.idempotency.key_reused"),
        RetryDirective.Never,
        [new PreconditionFailureDetail([new PreconditionViolation("idempotency_key", "request", "KEY_REUSED",
            new FailureMessageDescriptor("mpcore.idempotency.key_reused"))])]);

    private static FailureDescriptor Invalid(string code, string rule, string messageKey) => new(
        new ErrorIdentity(Domain, code),
        ErrorCategory.Validation,
        new FailureMessageDescriptor(messageKey),
        RetryDirective.Never,
        [new ValidationFailureDetail([new FieldViolation("idempotency_key", rule, new FailureMessageDescriptor(messageKey))])]);
}
