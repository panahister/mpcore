using System.Text.RegularExpressions;

namespace MPCore.Transport.Grpc.Tests;

/// <summary>
/// The review of the public API (docs/api/1.0-review.md) decides every public type of the runtime packages, once.
/// Three files must say the same thing: the table, each package's PublicAPI.Shipped.txt (the promise) and each
/// package's CompatibilitySuppressions.xml (the breaks against the last published version, each with its reason).
/// These tests hold them together, and the controls show the comparison sees each way they can come apart.
/// </summary>
public sealed partial class ApiReviewTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    [Fact]
    public void The_review_the_inventories_and_the_suppressions_say_the_same_thing()
    {
        var problems = Check(Review(), PublicTypes(), Suppressions());

        Assert.True(problems.Count == 0, "docs/api/1.0-review.md, the PublicAPI.Shipped.txt files and the CompatibilitySuppressions.xml files disagree:\n" + string.Join('\n', problems));
    }

    [Fact]
    public void The_counts_the_review_states_are_the_counts_of_its_table()
    {
        var rows = Review();
        var text = File.ReadAllText(Path.Combine(RepositoryRoot, "docs/api/1.0-review.md"));
        var stated = Regex.Matches(text, @"^\| (keep|make internal|remove) \| (\d+) \|$", RegexOptions.Multiline)
            .ToDictionary(static match => match.Groups[1].Value, static match => int.Parse(match.Groups[2].Value));

        Assert.Equal(rows.Count(static row => row.Decision == "keep"), stated["keep"]);
        Assert.Equal(rows.Count(static row => row.Decision == "make internal"), stated["make internal"]);
        Assert.Equal(rows.Count(static row => row.Decision == "remove"), stated["remove"]);
        Assert.Equal(rows.Count, PublicTypes().Count + rows.Count(static row => row.Decision != "keep"));
    }

    [Fact]
    public void Every_suppression_has_a_reason_above_it()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(RepositoryRoot, "src"), "CompatibilitySuppressions.xml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var entries = Regex.Matches(text, @"<Suppression>").Count;
            var reasons = Regex.Matches(text, @"<!-- 1\.0 review: [^>]+ -->\s*<Suppression>").Count;

            Assert.True(entries == reasons, $"{Path.GetRelativePath(RepositoryRoot, file)}: {entries} suppressions, {reasons} with a reason above them.");
        }
    }

    [Fact]
    public void A_type_the_table_leaves_out_is_found()
    {
        var rows = Review();
        var left = rows.First(static row => row.Decision == "keep");

        var problems = Check([.. rows.Where(row => row != left)], PublicTypes(), Suppressions());

        Assert.Contains(problems, problem => problem.Contains(left.Type, StringComparison.Ordinal) && problem.Contains("no row", StringComparison.Ordinal));
    }

    [Fact]
    public void A_type_decided_internal_that_is_still_public_is_found()
    {
        var rows = Review();
        var internalRow = rows.First(static row => row.Decision == "make internal");
        var stillPublic = new HashSet<string>(PublicTypes()) { internalRow.Type };

        var problems = Check(rows, stillPublic, Suppressions());

        Assert.Contains(problems, problem => problem.Contains(internalRow.Type, StringComparison.Ordinal) && problem.Contains("still public", StringComparison.Ordinal));
    }

    [Fact]
    public void A_type_made_internal_without_a_suppression_and_a_suppression_without_a_row_are_found()
    {
        var rows = Review();
        var suppressions = Suppressions();
        var internalRow = rows.First(static row => row.Decision == "make internal");
        var key = Normal(internalRow.Type);

        var withoutIt = new HashSet<string>(suppressions.Where(suppression => suppression != key));
        var withAnother = new HashSet<string>(suppressions) { "MPCore.Domain.NoSuchType" };

        Assert.Contains(Check(rows, PublicTypes(), withoutIt), problem => problem.Contains(internalRow.Type, StringComparison.Ordinal) && problem.Contains("no suppression", StringComparison.Ordinal));
        Assert.Contains(Check(rows, PublicTypes(), withAnother), problem => problem.Contains("MPCore.Domain.NoSuchType", StringComparison.Ordinal) && problem.Contains("no row", StringComparison.Ordinal));
    }

    private sealed record Row(string Package, string Type, string Decision);

    private static List<Row> Review()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot, "docs/api/1.0-review.md"));
        var rows = Regex.Matches(text, @"^\| `(?<package>MPCore\.[^`]+)` \| `(?<type>[^`]+)` \| (?<decision>keep|make internal|remove) \| (?<reason>.+) \|$", RegexOptions.Multiline)
            .Select(static match => new Row(match.Groups["package"].Value, match.Groups["type"].Value, match.Groups["decision"].Value))
            .ToList();
        Assert.NotEmpty(rows);
        Assert.Equal(rows.Count, rows.Select(static row => row.Type + "@" + row.Package).Distinct().Count());
        return rows;
    }

    /// <summary>Every type of every PublicAPI.Shipped.txt: a line with neither a signature nor a parameter list.</summary>
    private static HashSet<string> PublicTypes()
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(Path.Combine(RepositoryRoot, "src"), "PublicAPI.Shipped.txt", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadAllLines(file).Skip(1).Where(static line => line.Length > 0))
            {
                if (!line.Contains(" -> ", StringComparison.Ordinal) && !line.Contains('('))
                {
                    types.Add(line);
                }
            }
        }

        return types;
    }

    /// <summary>The types the CP0001 suppressions record, without generic arity.</summary>
    private static HashSet<string> Suppressions()
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(Path.Combine(RepositoryRoot, "src"), "CompatibilitySuppressions.xml", SearchOption.AllDirectories))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"<DiagnosticId>CP0001</DiagnosticId>\s*<Target>T:(?<target>[^<]+)</Target>"))
            {
                targets.Add(Normal(match.Groups["target"].Value));
            }
        }

        return targets;
    }

    private static string Normal(string type) => Regex.Replace(Regex.Replace(type, "<.*$", string.Empty), "`\\d+$", string.Empty);

    private static List<string> Check(List<Row> rows, HashSet<string> publicTypes, HashSet<string> suppressions)
    {
        var problems = new List<string>();
        var kept = rows.Where(static row => row.Decision == "keep").Select(static row => row.Type).ToHashSet(StringComparer.Ordinal);
        var gone = rows.Where(static row => row.Decision != "keep").Select(static row => Normal(row.Type)).ToHashSet(StringComparer.Ordinal);

        foreach (var type in publicTypes.Where(type => !kept.Contains(type)))
        {
            problems.Add($"{type} is public in a PublicAPI.Shipped.txt and the table has no row that keeps it.");
        }

        foreach (var type in kept.Where(type => !publicTypes.Contains(type)))
        {
            problems.Add($"{type} is kept by the table and is not in any PublicAPI.Shipped.txt.");
        }

        foreach (var row in rows.Where(static row => row.Decision != "keep"))
        {
            if (publicTypes.Contains(row.Type))
            {
                problems.Add($"{row.Type} is decided '{row.Decision}' and is still public.");
            }

            if (!suppressions.Contains(Normal(row.Type)))
            {
                problems.Add($"{row.Type} is decided '{row.Decision}' and has no suppression of the break.");
            }
        }

        foreach (var suppression in suppressions.Where(suppression => !gone.Contains(suppression)))
        {
            problems.Add($"{suppression} has a suppression and the table has no row that makes it internal or removes it.");
        }

        return problems;
    }
}
