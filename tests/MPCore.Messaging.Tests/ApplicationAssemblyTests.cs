using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using MPCore.Application.Time;
using MPCore.Messaging.Wolverine;
using Wolverine;
using Wolverine.Runtime;

namespace MPCore.Messaging.Tests;

/// <summary>
/// Wolverine discovers handlers in its "application assembly" in addition to the assemblies a host names.
/// It infers that assembly from whoever called <c>UseWolverine</c>, and MP Core makes that call on the host's
/// behalf — so the inference landed on <c>MPCore.Messaging.Wolverine</c>, and the host project's own
/// handlers (a Kafka consumer seam, say) were never discovered: their messages were consumed by nobody.
/// </summary>
public sealed class ApplicationAssemblyTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL");

    /// <summary>
    /// Built the way the generated template builds a host: <see cref="WebApplication.CreateBuilder()"/>, whose
    /// <c>Host</c> runs configuration immediately — while MP Core is on the call stack.
    /// </summary>
    private static async Task<WebApplication> StartAsync(System.Reflection.Assembly? applicationAssembly)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Host.UseMPCoreWolverine<PortLedgerContext>(
            new WolverineFoundationOptions
            {
                ServiceName = "application-assembly-tests",
                PersistenceConnectionString = ConnectionString!,
                PersistenceSchemaName = "wolverine_application_assembly_tests",
                ApplicationAssembly = applicationAssembly,
            },
            options =>
            {
                // Deliberately not the test assembly: its handlers may only arrive as the application's.
                options.DiscoverHandlersIn(typeof(MPCore.Application.Results.Result).Assembly);
                options.Durability.Mode = DurabilityMode.Solo;
            });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddMPCoreWolverineDbContext<PortLedgerContext>((_, options) =>
            options.UseNpgsql(ConnectionString).AddInterceptors(new SaveActorProbe()));
        builder.Services.AddScoped<IReservationRepository, ReservationRepository>();
        var app = builder.Build();
        await app.StartAsync();
        return app;
    }

    [PostgreSqlFact]
    public async Task The_host_assembly_it_names_is_where_its_own_handlers_are_discovered()
    {
        using var host = await StartAsync(typeof(ApplicationAssemblyTests).Assembly);
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PortLedgerContext>().Database.ExecuteSqlRawAsync(
                "CREATE TABLE IF NOT EXISTS port_test_reservations (\"Id\" uuid PRIMARY KEY, \"Holder\" text NOT NULL)");
        }

        HandlerActorObserved.InHandler = null;
        // StampReservationHandler lives only in this (the host's) assembly, which is not named for discovery.
        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new StampReservation(Guid.NewGuid()));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && HandlerActorObserved.InHandler is null)
        {
            await Task.Delay(100);
        }

        await host.StopAsync();
        Assert.Same(typeof(ApplicationAssemblyTests).Assembly, host.Services.GetRequiredService<IWolverineRuntime>().Options.ApplicationAssembly);
        Assert.Equal(nameof(StampReservation), HandlerActorObserved.InHandler);
    }

    [PostgreSqlFact]
    public async Task MP_Core_itself_is_never_taken_for_the_application()
    {
        using var host = await StartAsync(applicationAssembly: null);
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        Assert.NotSame(typeof(WolverineFoundationOptions).Assembly, runtime.Options.ApplicationAssembly);
        await host.StopAsync();
    }
}
