using System.Reflection;
using System.Text.RegularExpressions;
using MPCore.Domain.Model;

namespace MPCore.Domain.Tests;

/// <summary>
/// Every MP Core assembly names the commit it was built from in its informational version, as
/// <c>&lt;version&gt;+&lt;40-character commit&gt;</c>. A consumer that pins a commit reads it there or in the
/// package's nuspec; the release gate checks both on the packed bytes. This holds the build setting that
/// writes it (Source Link's <c>IncludeSourceRevisionInInformationalVersion</c>, on by default).
/// </summary>
public sealed partial class SourceRevisionTests
{
    [Fact]
    public void The_informational_version_names_the_version_and_the_full_source_commit()
    {
        var assembly = typeof(Entity<>).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.NotNull(informational);
        var match = InformationalVersion().Match(informational);
        Assert.True(match.Success, $"'{informational}' is not <version>+<40-character commit>.");

        var version = assembly.GetName().Version!;
        Assert.Equal($"{version.Major}.{version.Minor}.{version.Build}", match.Groups["prefix"].Value);
    }

    [GeneratedRegex(@"^(?<prefix>\d+\.\d+\.\d+)(-[0-9A-Za-z.-]+)?\+(?<commit>[0-9a-f]{40})$")]
    private static partial Regex InformationalVersion();
}
