namespace MPCore.Security.Tests;

public sealed class CurrentActorTests
{
    [Fact]
    public void Anonymous_actor_is_unauthenticated_and_carries_nothing()
    {
        var actor = CurrentActor.Anonymous;

        Assert.Equal(ActorKind.Anonymous, actor.Kind);
        Assert.False(actor.IsAuthenticated);
        Assert.Null(actor.SubjectId);
        Assert.Empty(actor.Scopes);
        Assert.Empty(actor.Roles);
        Assert.False(actor.HasScope("anything"));
        Assert.False(actor.HasRole("anything"));
    }

    [Fact]
    public void Authenticated_actor_requires_a_non_empty_subject()
    {
        Assert.Throws<InvalidOperationException>(() => new CurrentActorBuilder(ActorKind.User).Build());
        Assert.Throws<InvalidOperationException>(() =>
            new CurrentActorBuilder(ActorKind.Service) { SubjectId = "   " }.Build());
    }

    [Theory]
    [InlineData(nameof(CurrentActorBuilder.SubjectId))]
    [InlineData(nameof(CurrentActorBuilder.UserName))]
    [InlineData(nameof(CurrentActorBuilder.DisplayName))]
    [InlineData(nameof(CurrentActorBuilder.Email))]
    [InlineData(nameof(CurrentActorBuilder.PhoneNumber))]
    [InlineData(nameof(CurrentActorBuilder.SessionId))]
    [InlineData(nameof(CurrentActorBuilder.ClientId))]
    [InlineData(nameof(CurrentActorBuilder.Issuer))]
    public void String_members_are_bounded_at_256_characters(string member)
    {
        var oversized = new string('a', CurrentActor.MaximumMemberLength + 1);
        var builder = new CurrentActorBuilder(ActorKind.User) { SubjectId = "subject" };
        typeof(CurrentActorBuilder).GetProperty(member)!.SetValue(builder, oversized);

        var exception = Assert.Throws<ArgumentException>(builder.Build);
        Assert.Equal(member, exception.ParamName);
    }

    [Fact]
    public void Scope_and_role_counts_and_lengths_are_bounded()
    {
        var tooManyScopes = new CurrentActorBuilder(ActorKind.User) { SubjectId = "subject" };
        for (var index = 0; index <= CurrentActor.MaximumScopeCount; index++)
        {
            tooManyScopes.AddScope($"scope-{index}");
        }

        Assert.Equal(nameof(CurrentActorBuilder.Scopes), Assert.Throws<ArgumentException>(tooManyScopes.Build).ParamName);

        var tooManyRoles = new CurrentActorBuilder(ActorKind.User) { SubjectId = "subject" };
        for (var index = 0; index <= CurrentActor.MaximumRoleCount; index++)
        {
            tooManyRoles.AddRole($"role-{index}");
        }

        Assert.Equal(nameof(CurrentActorBuilder.Roles), Assert.Throws<ArgumentException>(tooManyRoles.Build).ParamName);

        var longRole = new CurrentActorBuilder(ActorKind.User) { SubjectId = "subject" }
            .AddRole(new string('r', CurrentActor.MaximumRoleLength + 1));
        Assert.Throws<ArgumentException>(longRole.Build);

        var longScope = new CurrentActorBuilder(ActorKind.User) { SubjectId = "subject" }
            .AddScope(new string('s', CurrentActor.MaximumScopeLength + 1));
        Assert.Throws<ArgumentException>(longScope.Build);
    }

    [Fact]
    public void Scopes_and_roles_are_ordinal_de_duplicated_and_defensively_copied()
    {
        var builder = new CurrentActorBuilder(ActorKind.User) { SubjectId = "subject" };
        builder.AddScope("catalog.read").AddScope("catalog.read").AddScope("Catalog.Read").AddScope("  ");
        builder.AddRole("platform-admin").AddRole("platform-admin").AddRole("Platform-Admin");

        var actor = builder.Build();
        builder.AddScope("added-after-build");

        Assert.Equal(new[] { "catalog.read", "Catalog.Read" }, actor.Scopes);
        Assert.Equal(new[] { "platform-admin", "Platform-Admin" }, actor.Roles);
        Assert.True(actor.HasScope("catalog.read"));
        Assert.False(actor.HasScope("CATALOG.READ"));
        Assert.True(actor.HasRole("platform-admin"));
        Assert.DoesNotContain("added-after-build", actor.Scopes);
    }

    [Fact]
    public void Actor_string_form_never_discloses_personal_data()
    {
        var actor = new CurrentActorBuilder(ActorKind.User)
        {
            SubjectId = "subject-1",
            Email = "person@example.invalid",
            PhoneNumber = "+000000000",
            SessionId = "session-1",
            UserName = "person"
        }.Build();

        var text = actor.ToString();

        Assert.DoesNotContain("person@example.invalid", text, StringComparison.Ordinal);
        Assert.DoesNotContain("+000000000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("session-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Actor_equality_compares_values_not_collection_references()
    {
        var first = new CurrentActorBuilder(ActorKind.User) { SubjectId = "subject" }
            .AddScope("catalog.read").Build();
        var second = new CurrentActorBuilder(ActorKind.User) { SubjectId = "subject" }
            .AddScope("catalog.read").Build();

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }
}
