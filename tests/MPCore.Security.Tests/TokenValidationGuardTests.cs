using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MPCore.Security.AspNetCore;

namespace MPCore.Security.Tests;

public sealed class TokenValidationGuardTests
{
    [Fact]
    public void A_missing_authority_fails_validation()
    {
        var result = Validate(Environments.Production, options => options.ValidAudiences.Add("catalog-api"));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure =>
            failure.Contains("Authority is required", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cleartext_authority_fails_outside_development()
    {
        var result = Validate(Environments.Production, options =>
        {
            options.Authority = "http://identity.invalid/realms/test";
            options.ValidAudiences.Add("catalog-api");
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("must use https", StringComparison.Ordinal));
    }

    [Fact]
    public void A_relative_authority_fails_validation()
    {
        var result = Validate(Environments.Production, options =>
        {
            options.Authority = "realms/test";
            options.ValidAudiences.Add("catalog-api");
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure =>
            failure.Contains("absolute URI", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_audiences_fail_validation()
    {
        var result = Validate(Environments.Production, options =>
            options.Authority = "https://identity.invalid/realms/test");

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure =>
            failure.Contains("ValidAudiences is required", StringComparison.Ordinal));
    }

    [Fact]
    public void The_provider_default_account_audience_is_rejected()
    {
        var result = Validate(Environments.Production, options =>
        {
            options.Authority = "https://identity.invalid/realms/test";
            options.ValidAudiences.Add("Account");
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure =>
            failure.Contains("does not identify this resource server", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("HS256")]
    [InlineData("HS512")]
    [InlineData("none")]
    [InlineData("")]
    public void Symmetric_and_unsigned_algorithms_are_rejected(string algorithm)
    {
        var result = Validate(Environments.Production, options =>
        {
            options.Authority = "https://identity.invalid/realms/test";
            options.ValidAudiences.Add("catalog-api");
            options.ValidAlgorithms.Clear();
            options.ValidAlgorithms.Add(algorithm);
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure =>
            failure.Contains("asymmetric algorithms only", StringComparison.Ordinal));
    }

    [Fact]
    public void Cleartext_metadata_is_rejected_outside_development_and_allowed_inside_it()
    {
        var production = Validate(Environments.Production, options =>
        {
            options.Authority = "https://identity.invalid/realms/test";
            options.RequireHttpsMetadata = false;
            options.ValidAudiences.Add("catalog-api");
        });
        var development = Validate(Environments.Development, options =>
        {
            options.Authority = "http://localhost:0/realms/test";
            options.RequireHttpsMetadata = false;
            options.ValidAudiences.Add("catalog-api");
        });

        Assert.True(production.Failed);
        Assert.Contains(production.Failures!, failure =>
            failure.Contains("only in the Development environment", StringComparison.Ordinal));
        Assert.True(development.Succeeded);
    }

    [Fact]
    public void An_out_of_range_clock_skew_is_rejected()
    {
        var result = Validate(Environments.Production, options =>
        {
            options.Authority = "https://identity.invalid/realms/test";
            options.ValidAudiences.Add("catalog-api");
            options.ClockSkew = TimeSpan.FromMinutes(30);
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure =>
            failure.Contains("ClockSkew", StringComparison.Ordinal));
    }

    [Fact]
    public void The_default_options_carry_no_realm_client_url_or_secret()
    {
        var options = new MPCoreBearerOptions();

        Assert.Null(options.Authority);
        Assert.Null(options.ValidIssuer);
        Assert.Null(options.MetadataAddress);
        Assert.Empty(options.ValidAudiences);
        Assert.True(options.RequireHttpsMetadata);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ClockSkew);
        Assert.Equal(MPCoreBearerOptions.AsymmetricAlgorithms, options.ValidAlgorithms);
        Assert.DoesNotContain(options.ValidAlgorithms, algorithm =>
            algorithm.StartsWith("HS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(algorithm, "none", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_configured_jwt_bearer_options_enforce_every_documented_guarantee()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = Environments.Production });
        services.AddMPCoreBearerAuthentication(options =>
        {
            options.Authority = "https://identity.invalid/realms/test";
            options.ValidAudiences.Add("catalog-api");
        });
        using var provider = services.BuildServiceProvider();

        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        var parameters = jwt.TokenValidationParameters;

        Assert.True(jwt.RequireHttpsMetadata);
        Assert.True(jwt.RefreshOnIssuerKeyNotFound);
        Assert.False(jwt.SaveToken);
        Assert.False(jwt.MapInboundClaims);
        Assert.True(parameters.ValidateIssuer);
        Assert.Equal("https://identity.invalid/realms/test", parameters.ValidIssuer);
        Assert.True(parameters.ValidateAudience);
        Assert.Equal(new[] { "catalog-api" }, parameters.ValidAudiences);
        Assert.True(parameters.ValidateLifetime);
        Assert.True(parameters.RequireExpirationTime);
        Assert.True(parameters.RequireSignedTokens);
        Assert.True(parameters.ValidateIssuerSigningKey);
        Assert.Equal(MPCoreBearerOptions.AsymmetricAlgorithms, parameters.ValidAlgorithms);
        Assert.Equal(TimeSpan.FromSeconds(30), parameters.ClockSkew);
        Assert.Equal("role", parameters.RoleClaimType);
        Assert.Equal("preferred_username", parameters.NameClaimType);
    }

    [Fact]
    public async Task A_misconfigured_host_fails_at_startup()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMPCoreBearerAuthentication(options =>
        {
            options.Authority = "https://identity.invalid/realms/test";
            options.RequireHttpsMetadata = false;
            options.ValidAudiences.Add("account");
        });

        await using var application = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => application.StartAsync());
        Assert.Contains(exception.Failures, failure =>
            failure.Contains("only in the Development environment", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, failure =>
            failure.Contains("does not identify this resource server", StringComparison.Ordinal));
    }

    private static ValidateOptionsResult Validate(
        string environmentName,
        Action<MPCoreBearerOptions> configure)
    {
        var options = new MPCoreBearerOptions();
        configure(options);
        var validator = new MPCoreBearerOptionsValidator(
            new HostingEnvironment { EnvironmentName = environmentName });
        return validator.Validate(Options.DefaultName, options);
    }

    private sealed class HostingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "MPCore.Security.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
