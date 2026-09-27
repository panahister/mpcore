using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Microsoft.EntityFrameworkCore;
using MPCore.Application.Idempotency;
using MPCore.Application.Results;
using MPCore.Persistence.Abstractions;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace MPCore.Messaging.Wolverine;

/// <summary>
/// Refuses to commit a unit of work whose handler ended in failure. A handler that returns a failed
/// <see cref="Result"/> after changing tracked state would otherwise commit those changes and release
/// its outgoing messages, because a returned value is not an error to the transactional middleware.
/// </summary>
/// <remarks>
/// A failure with nothing pending — not found, forbidden, a rejected precondition checked first — is
/// returned to the caller unchanged. Only a failure that arrives on top of pending changes is turned
/// into <see cref="ResultFailureException"/>, which rolls the transaction back and leaves the outbox
/// empty. Both transport adapters map that exception to the same status the returned failure would
/// have produced, so the caller sees one behaviour either way.
/// </remarks>
public static class ResultFailureRollback
{
    /// <summary>Applies the rule to a handler that returns a non-generic <see cref="Result"/>.</summary>
    /// <param name="result">The value the handler returned.</param>
    /// <param name="unitOfWork">The unit of work the handler declared.</param>
    public static void After(Result result, IUnitOfWork unitOfWork)
    {
        Enforce(result, unitOfWork);
        Remember(result, null);
    }

    // The handler succeeded and its change is about to be saved. When the command runs under an
    // idempotency key, the result is handed to the ambient operation here, so the persistence adapter can
    // write key and result in the very transaction that commits the change.
    internal static void Remember(Result? result, object? value)
    {
        if (result is { IsSuccess: true })
        {
            IdempotencyContext.Current?.Capture(value);
        }
        else
        {
            // The answer is a failure, so there is nothing to remember, and what an earlier attempt of
            // this operation left behind is not this attempt's answer either.
            IdempotencyContext.Current?.Forget();
        }
    }

    internal static void Enforce(Result? result, IUnitOfWork unitOfWork)
    {
        if (result is null || result.IsSuccess || !HasPendingChanges(unitOfWork))
        {
            return;
        }

        // Result's own constructor guarantees a descriptor on every failure, so this is not a guess.
        throw new ResultFailureException(result.FailureDescriptor!);
    }

    private static bool HasPendingChanges(IUnitOfWork unitOfWork) =>
        // Only an Entity Framework unit of work can be asked. Anything else is treated as pending,
        // because committing a failure is the outcome this rule exists to prevent.
        unitOfWork is not DbContext context || context.ChangeTracker.HasChanges();
}

/// <summary>
/// The same rule for a handler that returns <see cref="Result{TValue}"/>. Wolverine binds middleware
/// parameters by exact type, so the policy closes this class over each handler's own result type.
/// </summary>
/// <typeparam name="TValue">The success payload of the handler's result.</typeparam>
public static class ResultFailureRollback<TValue>
{
    /// <summary>Applies the rule to a handler that returns <see cref="Result{TValue}"/>.</summary>
    /// <param name="result">The value the handler returned.</param>
    /// <param name="unitOfWork">The unit of work the handler declared.</param>
    public static void After(Result<TValue> result, IUnitOfWork unitOfWork)
    {
        ResultFailureRollback.Enforce(result, unitOfWork);
        ResultFailureRollback.Remember(result, result is { IsSuccess: true } ? result.Value : null);
    }
}

/// <summary>
/// Attaches <see cref="ResultFailureRollback"/> to every handler that both declares a unit of work and
/// returns a result, closing the generic middleware over that handler's own result type.
/// </summary>
internal sealed class ResultFailureRollbackPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            foreach (var call in chain.HandlerCalls())
            {
                if (!DeclaresUnitOfWork(call.Method))
                {
                    // Without the port there is no transaction this rule could refuse, and adding a
                    // middleware that needs one would break an otherwise valid handler.
                    continue;
                }

                var middleware = MiddlewareFor(Unwrap(call.Method.ReturnType));
                if (middleware is not null)
                {
                    // A post-processor, never a "before" frame that merely depends on the handler's result:
                    // the code generator satisfies such a dependency by moving the handler call up to it, and
                    // every middleware registered after this policy (a product's, or FluentValidation's) then
                    // runs after the handler instead of before it. Found by the Storefront sample: an invalid command reached
                    // its handler and was validated afterwards.
                    //
                    // First among the post-processors: Wolverine's transactional policy has already queued
                    // SaveChangesAsync and the outbox commit there, and this verdict must come before them.
                    chain.Postprocessors.Insert(0, new MethodCall(middleware, nameof(ResultFailureRollback.After)));
                }
            }
        }
    }

    private static bool DeclaresUnitOfWork(System.Reflection.MethodBase method) =>
        method.GetParameters().Any(parameter => parameter.ParameterType == typeof(IUnitOfWork));

    private static Type? MiddlewareFor(Type returnType)
    {
        if (returnType == typeof(Result))
        {
            return typeof(ResultFailureRollback);
        }

        return returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Result<>)
            ? typeof(ResultFailureRollback<>).MakeGenericType(returnType.GenericTypeArguments[0])
            : null;
    }

    private static Type Unwrap(Type returnType) =>
        returnType.IsGenericType && (returnType.GetGenericTypeDefinition() == typeof(Task<>) || returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ? returnType.GenericTypeArguments[0]
            : returnType;
}
