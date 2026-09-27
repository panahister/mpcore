using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MPCore.Security.AspNetCore;

namespace MPCore.Security.Tests;

public sealed class ClaimMappingTests
{
    [Fact]
    public void Nested_realm_and_client_role_paths_are_extracted_with_the_wildcard_prefix()
    {
        var principal = Principal(
            new Claim("sub", "subject-1"),
            new Claim("realm_access", """{"roles":["platform-admin","auditor"]}"""),
            new Claim(
                "resource_access",
                """{"catalog-api":{"roles":["catalog-manager"]},"billing-api":{"roles":["billing-reader"]}}"""));

        var actor = CreateMapper(new ActorClaimMappingOptions()).Map(principal);

        Assert.Equal(
            new[] { "platform-admin", "auditor", "catalog-api:catalog-manager", "billing-api:billing-reader" },
            actor.Roles);
    }

    [Fact]
    public void Literal_and_none_prefix_strategies_are_honoured()
    {
        var options = new ActorClaimMappingOptions();
        options.RoleSources.Clear();
        options.RoleSources.Add(new RoleClaimSource { Path = "realm_access.roles" });
        options.RoleSources.Add(new RoleClaimSource
        {
            Path = "groups",
            Prefix = RolePrefixMode.Literal,
            LiteralPrefix = "group",
            PrefixSeparator = "/"
        });

        var actor = CreateMapper(options).Map(Principal(
            new Claim("sub", "subject-1"),
            new Claim("realm_access", """{"roles":["auditor"]}"""),
            new Claim("groups", """["finance"]""")));

        Assert.Equal(new[] { "auditor", "group/finance" }, actor.Roles);
    }

    [Fact]
    public void Malformed_provider_json_is_skipped_and_never_thrown()
    {
        var principal = Principal(
            new Claim("sub", "subject-1"),
            new Claim("realm_access", "{not-json"),
            new Claim("resource_access", """{"catalog-api":{"roles":["catalog-manager"]}}"""));

        var actor = CreateMapper(new ActorClaimMappingOptions()).Map(principal);

        Assert.Equal(new[] { "catalog-api:catalog-manager" }, actor.Roles);
    }

    [Fact]
    public void Unbounded_or_malformed_role_paths_are_skipped()
    {
        var options = new ActorClaimMappingOptions();
        options.RoleSources.Clear();
        options.RoleSources.Add(new RoleClaimSource { Path = string.Empty });
        options.RoleSources.Add(new RoleClaimSource { Path = "*.roles" });
        options.RoleSources.Add(new RoleClaimSource { Path = "a.*.b.*.c" });
        options.RoleSources.Add(new RoleClaimSource { Path = "realm_access.roles" });

        var actor = CreateMapper(options).Map(Principal(
            new Claim("sub", "subject-1"),
            new Claim("realm_access", """{"roles":["auditor"]}""")));

        Assert.Equal(new[] { "auditor" }, actor.Roles);
    }

    [Fact]
    public void At_most_eight_role_sources_are_evaluated()
    {
        var options = new ActorClaimMappingOptions();
        options.RoleSources.Clear();
        for (var index = 0; index < 12; index++)
        {
            options.RoleSources.Add(new RoleClaimSource { Path = $"source_{index}" });
        }

        var claims = new List<Claim> { new("sub", "subject-1") };
        for (var index = 0; index < 12; index++)
        {
            claims.Add(new Claim($"source_{index}", $"role-{index}"));
        }

        var actor = CreateMapper(options).Map(Principal([.. claims]));

        Assert.Equal(ActorClaimMappingOptions.MaximumRoleSources, actor.Roles.Count);
        Assert.Contains("role-7", actor.Roles);
        Assert.DoesNotContain("role-8", actor.Roles);
    }

    [Fact]
    public void Scopes_are_split_from_the_space_delimited_scope_claim()
    {
        var actor = CreateMapper(new ActorClaimMappingOptions()).Map(Principal(
            new Claim("sub", "subject-1"),
            new Claim("scope", "openid  catalog.read catalog.write")));

        Assert.Equal(new[] { "openid", "catalog.read", "catalog.write" }, actor.Scopes);
        Assert.True(actor.HasScope("catalog.write"));
    }

    [Fact]
    public void An_over_long_scope_is_skipped_without_discarding_the_remaining_scopes()
    {
        var overLong = new string('s', CurrentActor.MaximumScopeLength + 1);
        var actor = CreateMapper(new ActorClaimMappingOptions()).Map(Principal(
            new Claim("sub", "subject-1"),
            new Claim("scope", $"openid {overLong} catalog.read catalog.write")));

        // Aborting on the over-long entry would silently drop catalog.read and catalog.write and
        // produce a spurious 403 for a caller whose token actually carries them.
        Assert.Equal(new[] { "openid", "catalog.read", "catalog.write" }, actor.Scopes);
        Assert.DoesNotContain(overLong, actor.Scopes);
        Assert.True(actor.HasScope("catalog.write"));
    }

    [Fact]
    public void An_over_long_scope_in_a_json_array_is_skipped_without_discarding_the_rest()
    {
        var overLong = new string('s', CurrentActor.MaximumScopeLength + 1);
        var actor = CreateMapper(new ActorClaimMappingOptions()).Map(Principal(
            new Claim("sub", "subject-1"),
            new Claim("scope", $$"""["openid","{{overLong}}","catalog.read"]""")));

        Assert.Equal(new[] { "openid", "catalog.read" }, actor.Scopes);
    }

    [Fact]
    public void Scope_extraction_is_bounded_by_the_actor_scope_maximum()
    {
        var scopes = string.Join(
            ' ',
            Enumerable.Range(0, CurrentActor.MaximumScopeCount + 40).Select(index => $"scope.{index}"));

        var actor = CreateMapper(new ActorClaimMappingOptions()).Map(Principal(
            new Claim("sub", "subject-1"),
            new Claim("scope", scopes)));

        Assert.Equal(CurrentActor.MaximumScopeCount, actor.Scopes.Count);
        Assert.True(actor.HasScope("scope.0"));
        Assert.False(actor.HasScope($"scope.{CurrentActor.MaximumScopeCount}"));
    }

    [Fact]
    public void A_raw_role_claim_is_never_treated_as_a_role_by_the_mapper()
    {
        var actor = CreateMapper(new ActorClaimMappingOptions()).Map(Principal(
            new Claim("sub", "subject-1"),
            new Claim("role", "platform-admin"),
            new Claim("realm_access", """{"roles":["auditor"]}""")));

        Assert.Equal(new[] { "auditor" }, actor.Roles);
        Assert.False(actor.HasRole("platform-admin"));
    }

    [Fact]
    public void Standard_profile_claims_map_onto_the_actor()
    {
        var actor = CreateMapper(new ActorClaimMappingOptions()).Map(Principal(
            new Claim("sub", "subject-1"),
            new Claim("preferred_username", "operator"),
            new Claim("name", "Operator One"),
            new Claim("email", "operator@example.invalid"),
            new Claim("email_verified", "true"),
            new Claim("phone_number", "+000000000"),
            new Claim("phone_number_verified", "false"),
            new Claim("sid", "session-1"),
            new Claim("azp", "catalog-web"),
            new Claim("iss", TestIdentityProvider.Issuer),
            new Claim("exp", "4102444800")));

        Assert.Equal(ActorKind.User, actor.Kind);
        Assert.True(actor.IsAuthenticated);
        Assert.Equal("subject-1", actor.SubjectId);
        Assert.Equal("operator", actor.UserName);
        Assert.Equal("Operator One", actor.DisplayName);
        Assert.Equal("operator@example.invalid", actor.Email);
        Assert.True(actor.EmailVerified);
        Assert.False(actor.PhoneNumberVerified);
        Assert.Equal("session-1", actor.SessionId);
        Assert.Equal("catalog-web", actor.ClientId);
        Assert.Equal(TestIdentityProvider.Issuer, actor.Issuer);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), actor.ExpiresAt);
    }

    [Fact]
    public void A_machine_caller_without_a_user_name_is_a_service_actor()
    {
        var actor = CreateMapper(new ActorClaimMappingOptions()).Map(Principal(
            new Claim("sub", "service-account-subject"),
            new Claim("client_id", "catalog-worker")));

        Assert.Equal(ActorKind.Service, actor.Kind);
        Assert.Equal("catalog-worker", actor.ClientId);
    }

    [Fact]
    public void An_unauthenticated_or_subjectless_principal_maps_to_anonymous()
    {
        var mapper = CreateMapper(new ActorClaimMappingOptions());

        Assert.Equal(CurrentActor.Anonymous, mapper.Map(null));
        Assert.Equal(CurrentActor.Anonymous, mapper.Map(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Equal(CurrentActor.Anonymous, mapper.Map(Principal(new Claim("email", "x@example.invalid"))));
    }

    [Fact]
    public void Role_extraction_is_bounded_by_the_actor_role_maximum()
    {
        var options = new ActorClaimMappingOptions();
        options.RoleSources.Clear();
        options.RoleSources.Add(new RoleClaimSource { Path = "realm_access.roles" });

        var roles = string.Join(",", Enumerable.Range(0, 400).Select(index => $"\"role-{index}\""));
        var actor = CreateMapper(options).Map(Principal(
            new Claim("sub", "subject-1"),
            new Claim("realm_access", $$"""{"roles":[{{roles}}]}""")));

        Assert.Equal(CurrentActor.MaximumRoleCount, actor.Roles.Count);
    }

    private static ClaimsPrincipalActorMapper CreateMapper(ActorClaimMappingOptions options)
    {
        var wrapped = Options.Create(options);
        return new ClaimsPrincipalActorMapper(
            wrapped,
            new ActorRoleExtractor(wrapped, NullLogger<ActorRoleExtractor>.Instance));
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "TestBearer"));
}
