namespace MPCore.Application.Idempotency;

/// <summary>
/// Marks an endpoint whose callers must send an idempotency key. REST endpoints add it with
/// <c>RequireIdempotencyKey()</c>; a gRPC service method carries it as an attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequireIdempotencyKeyAttribute : Attribute;
