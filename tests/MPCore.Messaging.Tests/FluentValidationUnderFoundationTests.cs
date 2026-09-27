using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Results;
using MPCore.Application.Time;
using MPCore.Messaging.Wolverine;
using MPCore.Persistence.Abstractions;
using MPCore.Validation.FluentValidation;
using Wolverine;
using ResultFailureException = MPCore.Application.Results.ResultFailureException;

namespace MPCore.Messaging.Tests;

public sealed record OpenLedger(string? Holder) ;

public sealed class OpenLedgerValidator : AbstractValidator<OpenLedger>
{
    public OpenLedgerValidator() => RuleFor(x => x.Holder).NotEmpty();
}

public static class OpenLedgerHandler
{
    public static int Calls;

    // Declares IUnitOfWork like every writing handler in a product: that is what makes MP Core's
    // transaction and rollback policies apply to this chain.
    public static Result<string> Handle(OpenLedger command, IUnitOfWork unitOfWork)
    {
        Interlocked.Increment(ref Calls);
        return Result<string>.Success(command.Holder ?? "?");
    }
}

/// <summary>
/// The FluentValidation middleware must also run under the MP Core foundation (durable queues, EF
/// transactions, the rollback and actor policies), exactly as it does under plain Wolverine.
/// </summary>
public sealed class FluentValidationUnderFoundationTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");

    [PostgreSqlFact]
    public async Task An_invalid_message_is_refused_before_the_handler_under_the_foundation()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseMPCoreWolverine<PortLedgerContext>(
                new WolverineFoundationOptions
                {
                    ServiceName = "validation-foundation-tests",
                    PersistenceConnectionString = ConnectionString!,
                    PersistenceSchemaName = "wolverine_validation_tests",
                },
                options =>
                {
                    options.DiscoverHandlersIn(typeof(FluentValidationUnderFoundationTests).Assembly);
                    options.Durability.Mode = DurabilityMode.Solo;
                    options.UseMPCoreFluentValidation();
                })
            .ConfigureServices(services =>
            {
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IClock, SystemClock>();
                services.AddMPCoreWolverineDbContext<PortLedgerContext>((_, options) => options.UseNpgsql(ConnectionString));
                services.AddMPCoreValidators(typeof(FluentValidationUnderFoundationTests).Assembly);
            })
            .Build();
        await host.StartAsync();
        try
        {
            var before = OpenLedgerHandler.Calls;
            var exception = await Assert.ThrowsAsync<ResultFailureException>(() =>
                host.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result<string>>(new OpenLedger(null)));

            Assert.Equal(before, OpenLedgerHandler.Calls);
            Assert.Equal(ErrorCategory.Validation, exception.Failure.Category);

            var ok = await host.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result<string>>(new OpenLedger("sara"));
            Assert.Equal("sara", ok.Value);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
