using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Results;
using MPCore.Validation.FluentValidation;
using Wolverine;
using ResultFailureException = MPCore.Application.Results.ResultFailureException;

namespace MPCore.Validation.Tests;

public sealed record RegisterCustomer(string? Email, string? Name);

public sealed class RegisterCustomerValidator : AbstractValidator<RegisterCustomer>
{
    public RegisterCustomerValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(20);
    }
}

public static class RegisterCustomerHandler
{
    public static int Calls;

    public static Result<string> Handle(RegisterCustomer command)
    {
        Interlocked.Increment(ref Calls);
        return Result<string>.Success(command.Name!);
    }
}

/// <summary>
/// Validators run before the handler, inside Wolverine's generated code, and an invalid message never
/// reaches the handler. The caller receives MP Core's validation failure, not FluentValidation's exception.
/// </summary>
public sealed class WolverineValidationTests
{
    private static IHost Build() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(options =>
            {
                // Wolverine 6 already refuses service location in generated handler code (NotAllowed),
                // exactly as in an MP Core host, so the validators must be buildable from their registrations.
                options.Discovery.IncludeAssembly(typeof(WolverineValidationTests).Assembly);
                options.UseMPCoreFluentValidation();
            })
            .ConfigureServices(services => services.AddMPCoreValidators(typeof(WolverineValidationTests).Assembly))
            .Build();

    [Fact]
    public async Task An_invalid_message_is_refused_before_the_handler_runs()
    {
        using var host = Build();
        await host.StartAsync();
        var before = RegisterCustomerHandler.Calls;

        var exception = await Assert.ThrowsAsync<ResultFailureException>(() =>
            host.Services.GetRequiredService<IMessageBus>().InvokeAsync<Result<string>>(new RegisterCustomer("nope", "")));

        Assert.Equal(before, RegisterCustomerHandler.Calls);
        Assert.Equal(ErrorCategory.Validation, exception.Failure.Category);
        var violations = Assert.IsType<ValidationFailureDetail>(Assert.Single(exception.Failure.Details)).Violations;
        Assert.Contains(violations, v => v.FieldPath == "email" && v.RuleCode == "EMAIL");
        Assert.Contains(violations, v => v.FieldPath == "name" && v.RuleCode == "NOT_EMPTY");
        await host.StopAsync();
    }

    [Fact]
    public async Task A_valid_message_reaches_the_handler()
    {
        using var host = Build();
        await host.StartAsync();

        var result = await host.Services.GetRequiredService<IMessageBus>()
            .InvokeAsync<Result<string>>(new RegisterCustomer("sara@example.com", "Sara"));

        Assert.True(result.IsSuccess);
        Assert.Equal("Sara", result.Value);
        await host.StopAsync();
    }
}
