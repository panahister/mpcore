using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MPCore.Security.AspNetCore;

namespace MPCore.Security.Tests;

/// <summary>
/// <c>AuthenticatedAt</c> is when the person authenticated (OpenID Connect Core 1.0, section 2, <c>auth_time</c>),
/// never when the token was issued; <c>IssuedAt</c> carries <c>iat</c>. A product reads a claim MP Core does not
/// map through an allowlisted, bounded map, never through the token.
/// </summary>
public sealed class AuthenticationTimeAndClaimsTests
{
    private static readonly DateTimeOffset AuthenticatedAt = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
    private static readonly DateTimeOffset IssuedAt = AuthenticatedAt.AddMinutes(40);

    [Fact]
    public void A_token_with_auth_time_gives_the_authentication_time_and_the_issue_time_apart()
    {
        var actor = Map(new ActorClaimMappingOptions(), Claim("auth_time", AuthenticatedAt), Claim("iat", IssuedAt));

        Assert.Equal(AuthenticatedAt, actor.AuthenticatedAt);
        Assert.Equal(IssuedAt, actor.IssuedAt);
    }

    [Fact]
    public void A_token_without_auth_time_has_no_authentication_time_and_is_never_fresh()
    {
        // A refreshed token carries a new iat and no auth_time. It used to look freshly authenticated.
        var actor = Map(new ActorClaimMappingOptions(), Claim("iat", IssuedAt));

        Assert.Null(actor.AuthenticatedAt);
        Assert.Equal(IssuedAt, actor.IssuedAt);
        Assert.False(actor.IsAuthenticationFresh(TimeSpan.FromDays(365), IssuedAt));
    }

    [Theory]
    [InlineData(2, 5, true)]
    [InlineData(6, 5, false)]
    [InlineData(-10, 5, false)]
    [InlineData(0, 5, true)]
    public void Freshness_is_measured_from_the_authentication_time(int minutesAgo, int maximumMinutes, bool fresh)
    {
        var actor = Map(new ActorClaimMappingOptions(), Claim("auth_time", AuthenticatedAt));

        Assert.Equal(fresh, actor.IsAuthenticationFresh(TimeSpan.FromMinutes(maximumMinutes), AuthenticatedAt.AddMinutes(minutesAgo)));
    }

    [Fact]
    public void An_allowlisted_claim_is_exposed_and_a_claim_not_on_the_list_is_not()
    {
        var options = new ActorClaimMappingOptions();
        options.AdditionalClaims.Add("acr");
        options.AdditionalClaims.Add("org_unit");

        var actor = Map(
            options,
            new Claim("acr", "phr"),
            new Claim("org_unit", "branch-12"),
            new Claim("typ", "Bearer"),
            new Claim("internal_note", "not for the product"));

        Assert.Equal(new Dictionary<string, string> { ["acr"] = "phr", ["org_unit"] = "branch-12" }, actor.AdditionalClaims);
        Assert.False(actor.AdditionalClaims.ContainsKey("typ"));
        Assert.False(actor.AdditionalClaims.ContainsKey("internal_note"));
    }

    [Fact]
    public void An_allowlisted_claim_that_is_over_long_or_has_several_values_is_not_exposed()
    {
        var options = new ActorClaimMappingOptions();
        options.AdditionalClaims.Add("long");
        options.AdditionalClaims.Add("several");

        var actor = Map(
            options,
            new Claim("long", new string('x', CurrentActor.MaximumMemberLength + 1)),
            new Claim("several", "one"),
            new Claim("several", "two"));

        Assert.Empty(actor.AdditionalClaims);
    }

    [Fact]
    public void Without_an_allowlist_no_additional_claim_is_exposed()
    {
        var actor = Map(new ActorClaimMappingOptions(), new Claim("acr", "phr"));

        Assert.Empty(actor.AdditionalClaims);
    }

    [Fact]
    public void The_builder_bounds_the_map_and_the_actor_keeps_a_copy()
    {
        var builder = new CurrentActorBuilder(ActorKind.User) { SubjectId = "subject-1" };
        for (var index = 0; index <= CurrentActor.MaximumAdditionalClaimCount; index++)
        {
            builder.AdditionalClaims[$"claim-{index}"] = "value";
        }

        Assert.Throws<ArgumentException>(builder.Build);

        builder.AdditionalClaims.Clear();
        builder.AdditionalClaims["acr"] = "phr";
        var actor = builder.Build();
        builder.AdditionalClaims["acr"] = "changed";

        Assert.Equal("phr", actor.AdditionalClaims["acr"]);
        Assert.DoesNotContain("phr", actor.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_actors_differ_when_their_issue_time_or_claims_differ()
    {
        CurrentActor Build(DateTimeOffset? issuedAt, string acr)
        {
            var builder = new CurrentActorBuilder(ActorKind.User) { SubjectId = "subject-1", IssuedAt = issuedAt };
            builder.AdditionalClaims["acr"] = acr;
            return builder.Build();
        }

        Assert.Equal(Build(IssuedAt, "phr"), Build(IssuedAt, "phr"));
        Assert.NotEqual(Build(IssuedAt, "phr"), Build(IssuedAt, "pwd"));
        Assert.NotEqual(Build(IssuedAt, "phr"), Build(null, "phr"));
    }

    [Fact]
    public void A_host_that_allowlists_too_many_claims_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddMPCoreCurrentActor(options =>
        {
            for (var index = 0; index <= CurrentActor.MaximumAdditionalClaimCount; index++)
            {
                options.AdditionalClaims.Add($"claim-{index}");
            }
        });
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ActorClaimMappingOptions>>().Value);
    }

    private static CurrentActor Map(ActorClaimMappingOptions options, params Claim[] claims)
    {
        var wrapped = Options.Create(options);
        var mapper = new ClaimsPrincipalActorMapper(wrapped, new ActorRoleExtractor(wrapped, NullLogger<ActorRoleExtractor>.Instance));
        return mapper.Map(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "subject-1"), .. claims], "TestBearer")));
    }

    private static Claim Claim(string type, DateTimeOffset value) => new(type, value.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
}
