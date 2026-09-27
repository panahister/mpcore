using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Persistence.Abstractions;
using MPCore.Persistence.EntityFrameworkCore.PostgreSql;
using Xunit;

namespace MPCore.Persistence.Tests;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, TimeProvider timeProvider) : MPCoreDbContext(options, timeProvider);

public sealed class ReadOnlyProjectionContext(DbContextOptions<ReadOnlyProjectionContext> options) : DbContext(options);

public sealed class UnitOfWorkRegistrationTests
{
    private static ServiceProvider Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        configure(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_unit_of_work_port_resolves_to_the_same_scoped_context_instance()
    {
        // The registration a generated project gets. Without it an application handler cannot depend on
        // the port at all, which is what pushed handlers back onto the concrete DbContext.
        using var provider = Build(services => services.AddMPCorePostgreSql<AppDbContext>("Host=localhost;Database=unused"));
        using var scope = provider.CreateScope();

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Same(context, unitOfWork);

        // A second scope is a second instance: the port never leaks a context across requests.
        using var other = provider.CreateScope();
        Assert.NotSame(unitOfWork, other.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    [Fact]
    public void The_options_overload_registers_the_port_as_well()
    {
        using var provider = Build(services => services.AddMPCorePostgreSql<AppDbContext>(
            "Host=localhost;Database=unused", (_, options) => options.EnableDetailedErrors()));
        using var scope = provider.CreateScope();
        Assert.Same(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    [Fact]
    public void A_context_that_is_not_a_unit_of_work_registers_no_port()
    {
        using var provider = Build(services =>
        {
            services.AddDbContext<ReadOnlyProjectionContext>(options => options.UseInMemoryDatabase("projection"));
            services.AddMPCoreUnitOfWork<ReadOnlyProjectionContext>();
        });
        using var scope = provider.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService<IUnitOfWork>());
    }

    [Fact]
    public void An_application_that_already_chose_its_own_unit_of_work_keeps_it()
    {
        using var provider = Build(services =>
        {
            services.AddScoped<IUnitOfWork, DeliberateUnitOfWork>();
            services.AddMPCorePostgreSql<AppDbContext>("Host=localhost;Database=unused");
        });
        using var scope = provider.CreateScope();
        Assert.IsType<DeliberateUnitOfWork>(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    private sealed class DeliberateUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
