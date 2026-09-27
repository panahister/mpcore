using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MPCore.Localization.Tests;

/// <summary>
/// Every message key MP Core itself emits has a default text and a Persian translation. A key without
/// one would reach the caller as a problem document with no <c>detail</c>.
/// </summary>
public sealed partial class MPCoreMessagesCoverageTests
{
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MPCore.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("MPCore.sln not found above the test output.");
    }

    private static HashSet<string> Keys(string file) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "src/MPCore.Localization/Resources", file))
            .Root!.Elements("data").Select(static data => (string)data.Attribute("name")!).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_key_MP_Core_emits_has_a_default_text()
    {
        var emitted = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(static path => EmittedKeys(File.ReadAllText(path)))
            .ToHashSet(StringComparer.Ordinal);
        var defaults = Keys("MPCoreMessages.resx");

        Assert.NotEmpty(emitted);
        Assert.Empty(emitted.Except(defaults).Order());
    }

    [Fact]
    public void The_Persian_file_translates_exactly_the_default_keys()
    {
        Assert.Equal(Keys("MPCoreMessages.resx").Order(), Keys("MPCoreMessages.fa.resx").Order());
    }

    // Only literals used as message keys: the arguments of a FailureMessageDescriptor, and constants
    // named ...MessageKey or the Generic descriptor. Error domains such as "mpcore.http" are not keys.
    private static IEnumerable<string> EmittedKeys(string source) =>
        DescriptorArguments().Matches(source).Select(static match => match.Groups[1].Value)
            .Concat(KeyConstants().Matches(source).Select(static match => match.Groups[1].Value))
            .SelectMany(static text => KeyLiteral().Matches(text).Select(static match => match.Groups[1].Value));

    [GeneratedRegex("FailureMessageDescriptor\\(([^)]*)\\)", RegexOptions.Singleline)]
    private static partial Regex DescriptorArguments();

    [GeneratedRegex("(?:MessageKey|Generic[^=\\n]*)\\s*=\\s*(?:new\\()?\\s*(\"mpcore\\.[^\"]+\")")]
    private static partial Regex KeyConstants();

    [GeneratedRegex("\"(mpcore\\.[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)*)\"")]
    private static partial Regex KeyLiteral();
}
