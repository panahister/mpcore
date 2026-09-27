using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MPCore.Audit.Tests;

/// <summary>
/// EF Core and EF Core Relational must resolve to the same version in every graph that uses MP Core's
/// EF packages. Relational is not referenced by consumers directly, so if MP Core stops pinning it, it
/// silently falls back to the older patch its transitive parents ask for, and a consumer that also
/// compiles against the newer one (for example through Microsoft.EntityFrameworkCore.Design) fails
/// with a version conflict.
/// </summary>
public sealed class EntityFrameworkVersionAlignmentTests
{
    [Fact]
    public void Relational_resolves_to_the_same_version_as_EF_Core()
    {
        var core = typeof(DbContext).Assembly.GetName().Version;
        var relational = typeof(RelationalDatabaseFacadeExtensions).Assembly.GetName().Version;
        Assert.Equal(core, relational);
    }
}
