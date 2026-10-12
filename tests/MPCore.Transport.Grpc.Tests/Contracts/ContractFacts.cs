using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MPCore.Transport.Grpc.Tests.Contracts;

/// <summary>
/// A contract file is a JSON document of objects, strings, numbers, booleans and arrays of strings. It is read as
/// the set of facts it states, one per leaf: <c>commands.new backend.options.shape.default=service</c>,
/// <c>commands.new backend.options.shape.choices[]=modular-monolith</c>. A later contract is compatible with an
/// earlier one when it states every fact the earlier one stated: a fact that was added widens the contract, and a
/// fact that is gone, or changed, which is a fact gone and another added, breaks it.
/// </summary>
internal static class ContractFacts
{
    public static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    /// <summary>The first version that published a contract file. Before it, no earlier contract exists to compare with.</summary>
    public static readonly Version FirstContractVersion = new(0, 10, 2);

    public static string PathOf(string name) => Path.Combine(RepositoryRoot, "docs", "contracts", name);

    public static SortedSet<string> Of(JsonNode node)
    {
        var facts = new SortedSet<string>(StringComparer.Ordinal);
        Collect(node, string.Empty, facts);
        return facts;
    }

    public static Difference Compare(SortedSet<string> previous, SortedSet<string> current) =>
        new([.. previous.Except(current, StringComparer.Ordinal)], [.. current.Except(previous, StringComparer.Ordinal)]);

    /// <summary>
    /// While the major version is 0, a minor version may change what a consumer depends on (the release skill and
    /// the release notes of each version say what changed); a later major version may as well. A patch version may
    /// not. This is the rule until the compatibility policy of 1.0 replaces it.
    /// </summary>
    public static bool BreakAllowed(Version baseline, Version declared) =>
        declared.Major > baseline.Major || (declared.Major == 0 && baseline.Major == 0 && declared.Minor > baseline.Minor);

    public static Version DeclaredVersion() => VersionOf("<VersionPrefix>([^<]+)</VersionPrefix>");

    public static Version BaselineVersion() => VersionOf("<MPCoreBaselineVersion>([^<]+)</MPCoreBaselineVersion>");

    /// <summary>The contract file as published with the baseline version, or null when that tag has none.</summary>
    public static string? AtBaseline(string name)
    {
        var baseline = BaselineVersion();
        var (exitCode, output) = Git("show", $"v{baseline}:docs/contracts/{name}");
        if (exitCode == 0)
        {
            return output;
        }

        var (tagExit, _) = Git("rev-parse", "-q", "--verify", $"refs/tags/v{baseline}^{{commit}}");
        Assert.True(tagExit == 0, $"The tag v{baseline} is not in this clone: fetch the tags (git fetch --tags).");
        return null;
    }

    public static string Write(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

    /// <summary>
    /// Compares what the code declares with the checked-in file. With <c>MPCORE_UPDATE_CONTRACTS=1</c> the file is
    /// rewritten instead, on a person's machine; where <c>CI</c> is set, that is an error.
    /// </summary>
    public static void AssertFileIs(string name, JsonNode fromCode, string regenerate)
    {
        var path = PathOf(name);
        var generated = Write(fromCode);
        if (Environment.GetEnvironmentVariable("MPCORE_UPDATE_CONTRACTS") == "1")
        {
            Assert.True(
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")),
                "MPCORE_UPDATE_CONTRACTS=1 rewrites a contract file and must never be set where CI is.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, generated);
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing. Create it with: {regenerate}");
        var recorded = JsonNode.Parse(File.ReadAllText(path))!;
        var difference = Compare(Of(recorded), Of(fromCode));
        Assert.True(
            difference.IsEmpty && generated == File.ReadAllText(path).ReplaceLineEndings("\n"),
            $"docs/contracts/{name} is not what the code declares. An addition updates the file in the same commit; "
            + $"a removal or a change is a break (see docs/contracts/README.md).\n{difference}\nRegenerate it with: {regenerate}");
    }

    /// <summary>
    /// The contract in the checked-in file must state every fact the contract at the baseline version stated, unless
    /// the declared version may break it. Before the first contract was published there is nothing to compare with,
    /// and that is only true of a baseline older than <see cref="FirstContractVersion"/>.
    /// </summary>
    public static void AssertNothingPublishedIsBroken(string name)
    {
        var baseline = BaselineVersion();
        var published = AtBaseline(name);
        if (published is null)
        {
            Assert.True(
                baseline < FirstContractVersion,
                $"v{baseline} has no docs/contracts/{name}, but a contract was published from {FirstContractVersion} on.");
            return;
        }

        var difference = Compare(Of(JsonNode.Parse(published)!), Of(JsonNode.Parse(File.ReadAllText(PathOf(name)))!));
        Assert.True(
            difference.Removed.Count == 0 || BreakAllowed(baseline, DeclaredVersion()),
            $"docs/contracts/{name} drops or changes what v{baseline} stated, and {DeclaredVersion()} may not break it:\n{difference}");
    }

    private static void Collect(JsonNode? node, string path, SortedSet<string> facts)
    {
        switch (node)
        {
            case JsonObject members:
                foreach (var (key, value) in members)
                {
                    Collect(value, path.Length == 0 ? key : $"{path}.{key}", facts);
                }

                break;
            case JsonArray items:
                foreach (var item in items)
                {
                    facts.Add($"{path}[]={Scalar(item)}");
                }

                break;
            default:
                facts.Add($"{path}={Scalar(node)}");
                break;
        }
    }

    private static string Scalar(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(),
    };

    private static Version VersionOf(string pattern)
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot, "src/Directory.Build.props"));
        var text = Regex.Match(props, pattern).Groups[1].Value;
        return Version.Parse(Regex.Match(text, @"^\d+\.\d+\.\d+").Value);
    }

    private static (int ExitCode, string Output) Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    internal sealed record Difference(IReadOnlyList<string> Removed, IReadOnlyList<string> Added)
    {
        public bool IsEmpty => Removed.Count == 0 && Added.Count == 0;

        public override string ToString()
        {
            var text = new StringBuilder();
            foreach (var fact in Removed)
            {
                text.Append("  - ").AppendLine(fact);
            }

            foreach (var fact in Added)
            {
                text.Append("  + ").AppendLine(fact);
            }

            return text.ToString();
        }
    }
}
