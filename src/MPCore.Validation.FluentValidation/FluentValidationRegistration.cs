using System.Reflection;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ResultFailureException = MPCore.Application.Results.ResultFailureException;
using Wolverine;
using Wolverine.FluentValidation;

namespace MPCore.Validation.FluentValidation;

/// <summary>
/// Wolverine's FluentValidation middleware calls this when a message is invalid. MP Core throws its own
/// <see cref="ResultFailureException"/>, which REST renders as a 400 problem document with
/// <c>violations</c> and gRPC as <c>InvalidArgument</c> with <c>BadRequest</c> details. A queued message
/// that fails validation follows the host's rule for non-retryable failures.
/// </summary>
/// <typeparam name="T">The message type.</typeparam>
public sealed class ResultFailureValidationAction<T> : IFailureAction<T>
{
    /// <inheritdoc />
    public void Throw(T message, IReadOnlyList<ValidationFailure> failures) =>
        throw new ResultFailureException(ValidationFailures.ToFailure(failures));
}

/// <summary>Registration of FluentValidation for MP Core commands.</summary>
public static class MPCoreFluentValidationExtensions
{
    /// <summary>
    /// Runs every registered validator of a message before its handler, and turns failures into MP Core
    /// validation failures. Validators are registered explicitly, per module, with
    /// <see cref="AddMPCoreValidators"/>.
    /// </summary>
    /// <param name="options">The Wolverine options.</param>
    public static WolverineOptions UseMPCoreFluentValidation(this WolverineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.UseFluentValidation(RegistrationBehavior.ExplicitRegistration);
        options.Services.Replace(ServiceDescriptor.Singleton(typeof(IFailureAction<>), typeof(ResultFailureValidationAction<>)));
        return options;
    }

    /// <summary>
    /// Registers every validator in the assembly, by type. Singletons by default: an input validator
    /// checks the shape of a request and holds no state. A rule that needs the database is a business
    /// rule and belongs in the aggregate, not in a validator.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="assembly">The module assembly.</param>
    /// <param name="lifetime">The validator lifetime.</param>
    public static IServiceCollection AddMPCoreValidators(
        this IServiceCollection services,
        Assembly assembly,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);
        return services.AddValidatorsFromAssembly(assembly, lifetime, includeInternalTypes: true);
    }
}
