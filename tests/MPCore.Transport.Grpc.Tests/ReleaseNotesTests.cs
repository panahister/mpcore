using System.Text.RegularExpressions;
using MPCore.Transport.Grpc.Tests.Contracts;

namespace MPCore.Transport.Grpc.Tests;

/// <summary>
/// A product that moves from 0.9.3 to 1.0 reads one page: the table at the top of docs/releases/1.0.0.md lists every
/// behaviour change of every version since, with what to do and where it is explained. These tests read every release
/// note after 0.9.3 and fail when a change listed under its "Moving from", "Changed" or "Fixed" sections is missing
/// from that table, when a row says nothing to do, or when its link goes nowhere.
/// </summary>
public sealed partial class ReleaseNotesTests
{
    private const string Final = "1.0.0";

    private static readonly string Releases = Path.Combine(ContractFacts.RepositoryRoot, "docs", "releases");

    [Fact]
    public void Every_change_the_notes_list_since_0_9_3_is_in_the_table_of_the_final_notes()
    {
        var problems = Check(ReadAllNotes(), Table(File.ReadAllText(Path.Combine(Releases, Final + ".md"))));

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void A_change_the_table_leaves_out_is_found()
    {
        var notes = ReadAllNotes();
        var table = Table(File.ReadAllText(Path.Combine(Releases, Final + ".md")));
        var left = table.First(static row => row.Version == "0.10.0" && row.Change.StartsWith("A second call of", StringComparison.Ordinal));

        var problems = Check(notes, [.. table.Where(row => row != left)]);

        Assert.Contains(problems, static problem => problem.Contains("A second call of", StringComparison.Ordinal) && problem.Contains("0.10.0", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_without_what_to_do_and_a_link_that_goes_nowhere_are_found()
    {
        var notes = ReadAllNotes();
        var table = Table(File.ReadAllText(Path.Combine(Releases, Final + ".md")));
        var row = table[0];

        var empty = Check(notes, [row with { WhatToDo = string.Empty }, .. table.Skip(1)]);
        var brokenFile = Check(notes, [row with { Target = "0.10.0.md#no-such-section" }, .. table.Skip(1)]);
        var missingFile = Check(notes, [row with { Target = "9.9.9.md#new" }, .. table.Skip(1)]);

        Assert.Contains(empty, static problem => problem.Contains("what to do", StringComparison.Ordinal));
        Assert.Contains(brokenFile, static problem => problem.Contains("no-such-section", StringComparison.Ordinal));
        Assert.Contains(missingFile, static problem => problem.Contains("9.9.9.md", StringComparison.Ordinal));
    }

    [Fact]
    public void A_change_in_a_new_note_is_found_when_the_table_has_no_row_for_it()
    {
        var notes = ReadAllNotes();
        var table = Table(File.ReadAllText(Path.Combine(Releases, Final + ".md")));
        var invented = notes with { Changes = [.. notes.Changes, new Change("0.10.1", "A behaviour nobody listed")] };

        Assert.Contains(Check(invented, table), static problem => problem.Contains("A behaviour nobody listed", StringComparison.Ordinal));
    }

    private sealed record Change(string Version, string Key);

    private sealed record Notes(IReadOnlyList<Change> Changes, IReadOnlyDictionary<string, IReadOnlySet<string>> Anchors);

    private sealed record Row(string Version, string Change, string WhatToDo, string Target);

    private static Notes ReadAllNotes()
    {
        var changes = new List<Change>();
        var anchors = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(Releases, "*.md"))
        {
            var version = Path.GetFileNameWithoutExtension(path);
            var text = File.ReadAllText(path);
            anchors[Path.GetFileName(path)] = Headings(text);
            if (!Version.TryParse(version, out var parsed) || parsed <= new Version(0, 9, 3) || parsed > new Version(1, 0, 0))
            {
                continue;
            }

            foreach (var section in Regex.Split(text, @"\r?\n(?=## )"))
            {
                var heading = section.Split('\n', 2)[0].Trim();
                if (heading is "## Changed" or "## Fixed")
                {
                    foreach (Match row in Regex.Matches(section, @"^\| (?!-)(?<first>[^|]+?) \|", RegexOptions.Multiline))
                    {
                        var first = row.Groups["first"].Value.Trim();
                        if (first is not ("What" or "Area" or "Where"))
                        {
                            changes.Add(new Change(version, first));
                        }
                    }
                }
                else if (heading.StartsWith("## Moving from", StringComparison.Ordinal) && !(version == Final && heading == "## Moving from 0.9.3"))
                {
                    // The final notes restate the entries of 0.10.0 under "Moving from 0.9.3": they belong to 0.10.0.
                    foreach (Match bold in Regex.Matches(section, @"^\*\*(?<text>[^*\n]+?\.)\*\*", RegexOptions.Multiline))
                    {
                        changes.Add(new Change(version, bold.Groups["text"].Value.Trim()));
                    }
                }
            }
        }

        Assert.NotEmpty(changes);
        return new Notes(changes, anchors);
    }

    private static List<Row> Table(string text)
    {
        var start = text.IndexOf("## Every behaviour change since 0.9.3", StringComparison.Ordinal);
        Assert.True(start >= 0, "1.0.0.md has no table of every behaviour change since 0.9.3.");
        var end = text.IndexOf("\n## ", start + 5, StringComparison.Ordinal);
        var section = text[start..end];
        var rows = Regex.Matches(section, @"^\| (?<version>\d+\.\d+\.\d+) \| (?<change>.+?) \| (?<what>.*?) \| \[(?<label>[^\]]+)\]\((?<target>[^)]+)\) \|$", RegexOptions.Multiline)
            .Select(static match => new Row(match.Groups["version"].Value, match.Groups["change"].Value.Trim(), match.Groups["what"].Value.Trim(), match.Groups["target"].Value))
            .ToList();
        Assert.NotEmpty(rows);
        return rows;
    }

    private static HashSet<string> Headings(string text) =>
        [.. Regex.Matches(text, @"^#{1,6} (?<title>.+)$", RegexOptions.Multiline).Select(static match => Slug(match.Groups["title"].Value))];

    private static string Slug(string heading) =>
        Regex.Replace(Regex.Replace(heading.Trim().ToLowerInvariant(), @"[^a-z0-9 _-]", string.Empty), " ", "-");

    private static List<string> Check(Notes notes, List<Row> table)
    {
        var problems = new List<string>();
        foreach (var change in notes.Changes)
        {
            if (!table.Any(row => row.Version == change.Version && row.Change.Contains(change.Key, StringComparison.Ordinal)))
            {
                problems.Add($"{change.Version}: \"{change.Key}\" is listed in its notes and the table has no row for it.");
            }
        }

        foreach (var row in table)
        {
            if (string.IsNullOrWhiteSpace(row.WhatToDo))
            {
                problems.Add($"{row.Version}: \"{row.Change}\" says nothing about what to do.");
            }

            var hash = row.Target.IndexOf('#', StringComparison.Ordinal);
            var file = hash < 0 ? row.Target : row.Target[..hash];
            var anchor = hash < 0 ? string.Empty : row.Target[(hash + 1)..];
            var name = file.Length == 0 ? Final + ".md" : file;
            if (!notes.Anchors.TryGetValue(name, out var headings))
            {
                problems.Add($"{row.Version}: \"{row.Change}\" links {name}, which is not a release note.");
            }
            else if (anchor.Length > 0 && !headings.Contains(anchor))
            {
                problems.Add($"{row.Version}: \"{row.Change}\" links #{anchor}, which is not a heading of {name}.");
            }
        }

        return problems;
    }
}
