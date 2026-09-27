using MPCore.Domain.Model;
using MPCore.Domain.Rules;

namespace MPCore.Domain.Tests;

public sealed class BusinessRuleTests
{
    private sealed class QuantityMustBePositive(int quantity) : BusinessRule(
        "acme.ordering",
        "QUANTITY_NOT_POSITIVE",
        "ordering.quantity_not_positive",
        new Dictionary<string, string> { ["quantity"] = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture) })
    {
        public override bool IsBroken() => quantity <= 0;
    }

    private sealed class LegacyRule : IBusinessRule
    {
        public string Code => "LEGACY_RULE";

        public string Message => "legacy";

        public bool IsBroken() => true;
    }

    private sealed class Basket : AggregateRoot<Guid>
    {
        public Basket()
            : base(Guid.NewGuid())
        {
        }

        public int Quantity { get; private set; }

        public void SetQuantity(int quantity)
        {
            CheckRule(new QuantityMustBePositive(quantity));
            Quantity = quantity;
        }
    }

    [Fact]
    public void A_rule_carries_its_domain_code_key_and_arguments()
    {
        IBusinessRule rule = new QuantityMustBePositive(0);

        Assert.True(rule.IsBroken());
        Assert.Equal("acme.ordering", rule.ErrorDomain);
        Assert.Equal("QUANTITY_NOT_POSITIVE", rule.Code);
        Assert.Equal("ordering.quantity_not_positive", rule.MessageKey);
        Assert.Equal("0", rule.MessageArguments["quantity"]);
        Assert.DoesNotContain("0", rule.Message.Replace("QUANTITY_NOT_POSITIVE", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void An_aggregate_that_checks_a_broken_rule_does_not_change()
    {
        var basket = new Basket();
        basket.SetQuantity(2);

        var exception = Assert.Throws<BusinessRuleValidationException>(() => basket.SetQuantity(0));

        Assert.Equal("QUANTITY_NOT_POSITIVE", exception.Rule.Code);
        Assert.Equal(2, basket.Quantity);
    }

    [Fact]
    public void A_rule_implemented_directly_gets_safe_defaults()
    {
        IBusinessRule rule = new LegacyRule();

        Assert.Equal(BusinessRule.DefaultErrorDomain, rule.ErrorDomain);
        Assert.Equal("business_rule.legacy_rule", rule.MessageKey);
        Assert.Empty(rule.MessageArguments);
    }

    [Theory]
    [InlineData("Acme.Ordering", "CODE_A", "ordering.key")]
    [InlineData("acme.ordering", "code_a", "ordering.key")]
    [InlineData("acme.ordering", "CODE_A", "Ordering.Key")]
    [InlineData("acme.ordering", "CODE_A", "ordering key")]
    public void Malformed_identifiers_fail_when_the_rule_is_created(string domain, string code, string key)
    {
        Assert.Throws<ArgumentException>(() => new ConfigurableRule(domain, code, key, null));
    }

    [Fact]
    public void Arguments_are_bounded_like_the_failure_model()
    {
        var tooMany = Enumerable.Range(0, BusinessRule.MaximumArgumentCount + 1).ToDictionary(i => $"a{i}", i => "x");
        Assert.Throws<ArgumentException>(() => new ConfigurableRule("acme.ordering", "CODE_A", "ordering.key", tooMany));
        Assert.Throws<ArgumentException>(() => new ConfigurableRule(
            "acme.ordering", "CODE_A", "ordering.key", new Dictionary<string, string> { ["Bad-Key"] = "x" }));
        Assert.Throws<ArgumentException>(() => new ConfigurableRule(
            "acme.ordering", "CODE_A", "ordering.key",
            new Dictionary<string, string> { ["value"] = new string('x', BusinessRule.MaximumArgumentValueLength + 1) }));
    }

    private sealed class ConfigurableRule(string domain, string code, string key, IReadOnlyDictionary<string, string>? arguments)
        : BusinessRule(domain, code, key, arguments)
    {
        public override bool IsBroken() => false;
    }
}
