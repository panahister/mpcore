using MPCore.Audit;
using Xunit;

namespace MPCore.Audit.Tests;

public class AuditPolicyTests
{
    private class Account
    {
        public Guid Id { get; set; }
        public string HolderName { get; set; } = "";
        public string Iban { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string ApiToken { get; set; } = "";
    }

    private sealed class SpecialAccount : Account { }

    [Theory]
    [InlineData("PasswordHash")]
    [InlineData("ApiToken")]
    public void Credential_like_properties_cannot_be_allowlisted_even_masked(string name)
    {
        var policy = new AuditPolicy();
        var builder = policy.Entity<Account>("Banking");
        var exception = Assert.Throws<InvalidOperationException>(() =>
            name == "PasswordHash" ? builder.Mask(a => a.PasswordHash) : builder.Include(a => a.ApiToken));
        Assert.Contains("never recorded", exception.Message);
        Assert.Empty(policy.Entities[typeof(Account)].Properties);
    }

    [Fact]
    public void Banking_identifiers_are_masked_even_when_included_verbatim()
    {
        var policy = new AuditPolicy();
        policy.Entity<Account>("Banking").Include(a => a.Iban).Include(a => a.HolderName);
        var entity = policy.Entities[typeof(Account)];
        Assert.Equal(MaskStyle.Redact, entity.Properties["Iban"]);
        Assert.Null(entity.Properties["HolderName"]);
        Assert.True(entity.Required);
    }

    [Fact]
    public void Explicit_mask_style_wins_and_best_effort_is_opt_in()
    {
        var policy = new AuditPolicy();
        policy.Entity<Account>("Banking").Mask(a => a.Iban, MaskStyle.KeepLastFour).BestEffort();
        var entity = policy.Entities[typeof(Account)];
        Assert.Equal(MaskStyle.KeepLastFour, entity.Properties["Iban"]);
        Assert.False(entity.Required);
    }

    [Theory]
    [InlineData("IR820540102680020817909002", MaskStyle.KeepLastFour, "**********************9002")]
    [InlineData("1234", MaskStyle.KeepLastFour, "****")]
    [InlineData("anything", MaskStyle.Redact, "***")]
    [InlineData(null, MaskStyle.Redact, null)]
    public void Masking_reduces_values_without_leaking_length_beyond_style(string? value, MaskStyle style, string? expected)
    {
        Assert.Equal(expected, AuditMasking.Apply(value, style));
        Assert.Equal(value, AuditMasking.Apply(value, null));
    }

    [Fact]
    public void Policy_resolves_through_base_types_and_is_absent_for_unknown()
    {
        var policy = new AuditPolicy();
        policy.Entity<Account>("Banking").Include(a => a.HolderName);
        Assert.Same(policy.Entities[typeof(Account)], policy.Find(typeof(SpecialAccount)));
        Assert.Null(policy.Find(typeof(string)));
    }

    [Fact]
    public void Builder_rejects_non_property_expressions()
    {
        var policy = new AuditPolicy();
        Assert.Throws<ArgumentException>(() => policy.Entity<Account>("Banking").Include(a => a.HolderName.ToUpperInvariant()));
    }
}
