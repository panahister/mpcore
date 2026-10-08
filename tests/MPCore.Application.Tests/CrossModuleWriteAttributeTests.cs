using System.Reflection;
using MPCore.Application.Modules;

namespace MPCore.Application.Tests;

/// <summary>
/// A writing interface between modules carries the reason it is not a message. The attribute exists only to
/// be read, so it cannot be declared without a reason.
/// </summary>
public sealed class CrossModuleWriteAttributeTests
{
    [CrossModuleWrite("must change together")]
    private interface IDeclared;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_cross_module_write_cannot_be_declared_without_a_reason(string reason) =>
        Assert.Throws<ArgumentException>(() => new CrossModuleWriteAttribute(reason));

    [Fact]
    public void The_reason_is_read_from_the_interface() =>
        Assert.Equal("must change together", typeof(IDeclared).GetCustomAttribute<CrossModuleWriteAttribute>()!.Reason);

    [Fact]
    public void Only_an_interface_can_declare_it_and_only_once()
    {
        var usage = typeof(CrossModuleWriteAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;

        Assert.Equal(AttributeTargets.Interface, usage.ValidOn);
        Assert.False(usage.AllowMultiple);
        Assert.False(usage.Inherited);
    }
}
