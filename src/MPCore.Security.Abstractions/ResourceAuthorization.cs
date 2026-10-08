namespace MPCore.Security;

/// <summary>What the product's decision component says about an actor and a resource key.</summary>
/// <remarks>The default value is <see cref="Denied"/>: a decision nobody made denies.</remarks>
public enum ResourceDecision
{
    /// <summary>The actor may not act on the resource.</summary>
    Denied = 0,

    /// <summary>The actor may act on the resource.</summary>
    Granted = 1,

    /// <summary>The component does not know the key; the request is denied.</summary>
    UnknownKey = 2
}

/// <summary>
/// The product's decision whether the current actor may act on a resource key. MP Core asks it for every
/// endpoint that declares a key and holds no permission of its own (ADR-007, section 9, and its addendum on
/// resource keys).
/// </summary>
public interface IResourceAuthorizer
{
    /// <summary>Decides for one actor and one key.</summary>
    /// <param name="actor">The validated current actor.</param>
    /// <param name="resourceKey">The key the endpoint declares.</param>
    /// <param name="cancellationToken">Cancelled when the request ends or the decision takes too long.</param>
    ValueTask<ResourceDecision> DecideAsync(CurrentActor actor, string resourceKey, CancellationToken cancellationToken);
}
