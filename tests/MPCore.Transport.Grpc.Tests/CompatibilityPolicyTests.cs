using System.Text.RegularExpressions;
using MPCore.Transport.Grpc.Tests.Contracts;

namespace MPCore.Transport.Grpc.Tests;

/// <summary>
/// The compatibility promise of 1.0 is stated in README "Versioning" and reasoned in ADR-020. These tests hold the
/// statements the promise rests on: the support window is named and linked, the experimental APIs are listed, a
/// removal waits for a major version, and the places that teach an upgrade point at the policy. Each check is a pure
/// function over text, so that a control can show the check sees what it is for.
/// </summary>
public sealed class CompatibilityPolicyTests
{
    private const string Adr = "docs/decisions/ADR-020-the-compatibility-promise-of-1-0.EN.md";

    private static readonly string RepositoryRoot = ContractFacts.RepositoryRoot;

    [Fact]
    public void README_names_the_support_window_and_links_the_decision_that_gives_the_reasons()
    {
        var versioning = VersioningSection(Read("README.md"));

        var problems = ReadmeProblems(versioning);

        Assert.True(problems.Count == 0, string.Join('\n', problems));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, Adr)), $"{Adr} is missing.");
        Assert.Contains("../../README.md#versioning", Read(Adr), StringComparison.Ordinal);
    }

    [Fact]
    public void A_versioning_section_without_the_window_or_the_link_is_found()
    {
        var versioning = VersioningSection(Read("README.md"));

        Assert.Contains(ReadmeProblems(versioning.Replace("12 months", "a while", StringComparison.Ordinal)), static problem => problem.Contains("12 months", StringComparison.Ordinal));
        Assert.Contains(ReadmeProblems(versioning.Replace("ADR-020-the-compatibility-promise-of-1-0.EN.md", "ADR-010.md", StringComparison.Ordinal)), static problem => problem.Contains("ADR-020", StringComparison.Ordinal));
        Assert.Contains(ReadmeProblems(versioning.Replace("never past the end of support of the .NET version it", "for ever, whatever .NET version it", StringComparison.Ordinal)), static problem => problem.Contains("end of support", StringComparison.Ordinal));
    }

    [Fact]
    public void An_experimental_api_is_listed_in_README_with_its_diagnostic_id_and_nothing_else_is()
    {
        var sources = Directory.GetFiles(Path.Combine(RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText);

        var problems = ExperimentalProblems(VersioningSection(Read("README.md")), sources);

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void An_experimental_api_that_is_not_listed_and_a_listed_one_that_does_not_exist_are_found()
    {
        var versioning = VersioningSection(Read("README.md"));
        var unlisted = ExperimentalProblems(versioning, ["[Experimental(\"MPCOREEXP001\")]\npublic sealed class Preview { }", "[System.Diagnostics.CodeAnalysis.ExperimentalAttribute(\"MPCOREEXP003\")]\npublic sealed class Other { }"]);
        Assert.Contains(unlisted, static problem => problem.Contains("MPCOREEXP003", StringComparison.Ordinal));
        var missing = ExperimentalProblems(versioning.Replace("| none at 1.0.0 | |", "| MPCOREEXP002 | `Preview` |", StringComparison.Ordinal), []);

        Assert.Contains(unlisted, static problem => problem.Contains("MPCOREEXP001", StringComparison.Ordinal) && problem.Contains("not listed", StringComparison.Ordinal));
        Assert.Contains(missing, static problem => problem.Contains("MPCOREEXP002", StringComparison.Ordinal) && problem.Contains("no API", StringComparison.Ordinal));
    }

    [Fact]
    public void A_removal_waits_for_a_major_version()
    {
        var removals = RemovedInUnshipped();
        var baseline = ContractFacts.BaselineVersion();
        var declared = ContractFacts.DeclaredVersion();

        Assert.True(
            removals.Count == 0 || declared.Major > baseline.Major,
            $"{declared} removes public API after {baseline}, and only a new major version may:\n{string.Join('\n', removals)}");
    }

    [Fact]
    public void A_removal_in_a_minor_version_is_found_and_in_a_major_version_is_allowed()
    {
        var removed = new[] { "*REMOVED*MPCore.Domain.Rules.BusinessRule" };

        Assert.Single(RemovalsAllowed(removed, Version.Parse("1.0.0"), Version.Parse("1.1.0")));
        Assert.Empty(RemovalsAllowed(removed, Version.Parse("1.4.2"), Version.Parse("2.0.0")));
        Assert.Empty(RemovalsAllowed([], Version.Parse("1.0.0"), Version.Parse("1.0.1")));
    }

    [Fact]
    public void No_decision_record_reads_proposed()
    {
        var problems = ProposedIn(Directory.GetFiles(Path.Combine(RepositoryRoot, "docs/decisions"), "*.md").ToDictionary(static path => Path.GetFileName(path), File.ReadAllText));

        Assert.True(problems.Count == 0, "A decision that reads proposed is a decision nobody has made:\n" + string.Join('\n', problems));
    }

    [Fact]
    public void A_record_or_an_addendum_that_reads_proposed_is_found()
    {
        var problems = ProposedIn(new Dictionary<string, string>
        {
            ["ADR-900.md"] = "# ADR-900\n\n- Status: Proposed; pending owner acceptance\n",
            ["ADR-901.md"] = "# ADR-901\n\n- Status: Accepted, 2026-10-12\n\n## Addendum 2026-10-12: a thing (proposed; pending owner acceptance)\n",
            ["ADR-902.md"] = "# ADR-902\n\n- Status: Accepted, 2026-10-12\n\nStatus: proposed. It changes the first point.\n",
            ["ADR-903.md"] = "# ADR-903\n\n- Status: Accepted, 2026-10-12\n\n## Addendum 2026-10-12: a thing (accepted by the repository owner, 2026-10-12)\n",
        });

        Assert.Equal(["ADR-900.md", "ADR-901.md", "ADR-902.md"], problems.Select(static problem => problem.Split(':')[0]).ToArray());
    }

    [Theory]
    [InlineData("tools/MPCore.Templates/content/MPCore.Backend/docs/getting-started.md")]
    [InlineData(".mpcore/skills/mpcore-evolve-package/SKILL.md")]
    [InlineData(".mpcore/skills/mpcore-release/SKILL.md")]
    public void The_places_that_teach_an_upgrade_point_at_the_policy(string file)
    {
        var text = Read(file);

        Assert.True(
            text.Contains("ADR-020", StringComparison.Ordinal) && (text.Contains("#versioning", StringComparison.Ordinal) || text.Contains("Versioning", StringComparison.Ordinal)),
            $"{file} does not point at the compatibility policy (README, Versioning; ADR-020).");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepositoryRoot, relative));

    private static string VersioningSection(string readme)
    {
        var start = readme.IndexOf("\n## Versioning\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "README has no Versioning section.");
        var end = readme.IndexOf("\n## ", start + 5, StringComparison.Ordinal);
        return readme[start..(end < 0 ? readme.Length : end)];
    }

    private static List<string> ReadmeProblems(string versioning)
    {
        var problems = new List<string>();
        if (!versioning.Contains("12 months", StringComparison.Ordinal))
        {
            problems.Add("README, Versioning, does not name the support window of 12 months.");
        }

        if (!versioning.Contains("end of support of the .NET version", StringComparison.Ordinal))
        {
            problems.Add("README, Versioning, does not bound the window by the end of support of the .NET version.");
        }

        if (!versioning.Contains("docs/decisions/ADR-020-the-compatibility-promise-of-1-0.EN.md", StringComparison.Ordinal))
        {
            problems.Add("README, Versioning, does not link ADR-020.");
        }

        if (!versioning.Contains("[Obsolete]", StringComparison.Ordinal) || !versioning.Contains("[Experimental]", StringComparison.Ordinal))
        {
            problems.Add("README, Versioning, does not state the [Obsolete] rule and the [Experimental] exception.");
        }

        return problems;
    }

    private static List<string> ExperimentalProblems(string versioning, IEnumerable<string> sources)
    {
        var used = sources
            .SelectMany(static text => Regex.Matches(text, @"(?<![A-Za-z0-9_])(?:[A-Za-z0-9_]+\.)*Experimental(?:Attribute)?\(\s*""(?<id>[A-Za-z0-9_]+)""").Select(static match => match.Groups["id"].Value))
            .ToHashSet(StringComparer.Ordinal);
        var listed = Regex.Matches(versioning, @"^\| (?<id>[A-Z][A-Z0-9_]+) \|", RegexOptions.Multiline)
            .Select(static match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);
        var problems = used.Where(id => !listed.Contains(id)).Select(static id => $"{id} marks an experimental API and is not listed in README, Versioning.").ToList();
        problems.AddRange(listed.Where(id => !used.Contains(id)).Select(static id => $"{id} is listed in README, Versioning, and no API in src is marked with it."));
        return problems;
    }

    private static List<string> RemovedInUnshipped()
    {
        var removed = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(RepositoryRoot, "src"), "PublicAPI.Unshipped.txt", SearchOption.AllDirectories))
        {
            removed.AddRange(File.ReadAllLines(file).Where(static line => line.StartsWith("*REMOVED*", StringComparison.Ordinal)).Select(line => $"{Path.GetRelativePath(RepositoryRoot, file)}: {line}"));
        }

        return removed;
    }

    private static List<string> ProposedIn(IReadOnlyDictionary<string, string> records) =>
        [.. records.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Where(static pair => Regex.IsMatch(pair.Value, @"^(?:- )?Status: proposed|\(proposed[;)]", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            .Select(static pair => $"{pair.Key}: reads proposed")];

    private static List<string> RemovalsAllowed(IEnumerable<string> removed, Version baseline, Version declared) =>
        declared.Major > baseline.Major ? [] : [.. removed];
}
