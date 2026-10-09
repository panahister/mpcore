using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace MPCore.Localization.Tests;

/// <summary>
/// English is MP Core's only built-in language and its default. A product adds its own languages, or puts
/// another in English's place, in its own repository; MP Core carries none. These tests fail when another
/// language enters what MP Core ships or teaches: a culture-specific resource file in the source, the
/// template or the documentation; text in a script other than Latin anywhere in the repository, tests and
/// diagrams included; or a satellite assembly in the packed localization package.
/// </summary>
public sealed class EnglishOnlyTests
{
    private static readonly string[] ResourceExtensions = [".resx", ".restext", ".resources", ".resw"];

    private static readonly string[] TextExtensions =
    [
        ".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".md", ".resx", ".restext", ".json", ".xml",
        ".yaml", ".yml", ".sh", ".py", ".proto", ".svg", ".txt", ".http", ".html", ".css", ".js", ".ts",
        ".config", ".editorconfig", ".gitignore", ".gitattributes", ".toml", ".ps1",
    ];

    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", "artifacts", "node_modules", "TestResults", ".vs", ".idea"];

    [Fact]
    public void MP_Core_ships_and_teaches_no_culture_specific_resource_file()
    {
        var root = RepositoryRoot();
        var satellites = new[] { "src", "tools", "docs" }
            .SelectMany(folder => Files(Path.Combine(root, folder)))
            .Where(static path => ResourceExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(static path => CultureOf(path) is not null)
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            satellites.Count == 0,
            "MP Core's texts live in the neutral resource file, in English; a culture-specific file belongs to a product: "
            + string.Join(", ", satellites));
    }

    [Fact]
    public void No_text_in_the_repository_is_written_in_a_script_other_than_Latin()
    {
        var root = RepositoryRoot();
        var findings = new List<string>();
        foreach (var path in Files(root).Where(static path => TextExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
        {
            var number = 0;
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                number++;
                foreach (var rune in line.EnumerateRunes())
                {
                    if (Rune.IsLetter(rune) && !IsLatin(rune.Value))
                    {
                        findings.Add($"{Path.GetRelativePath(root, path)}:{number} U+{rune.Value:X4}");
                        break;
                    }
                }
            }
        }

        Assert.True(findings.Count == 0, "Text outside the Latin script: " + string.Join(", ", findings.Take(40)) + (findings.Count > 40 ? $" and {findings.Count - 40} more" : string.Empty));
    }

    // Packs a package inside the test, which restores packages from the network: it runs in the slow job of CI.
    [Fact]
    [Trait("Category", "Packaging")]
    public async Task The_packed_localization_package_carries_no_satellite_assembly()
    {
        var root = RepositoryRoot();
        var output = Directory.CreateTempSubdirectory("mpcore-english-only-").FullName;
        try
        {
            // Packed from the build these tests run against, with nothing rebuilt or restored; API validation
            // is the release gate's concern, not this test's.
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var (exitCode, log) = await RunAsync(
                root,
                "pack", "src/MPCore.Localization/MPCore.Localization.csproj", "--configuration", configuration,
                "--no-build", "--no-restore", "--output", output, "-p:EnablePackageValidation=false", "-nologo");
            Assert.True(exitCode == 0, "dotnet pack failed:\n" + log);

            var package = Assert.Single(Directory.GetFiles(output, "MPCore.Localization.*.nupkg"));
            using var archive = ZipFile.OpenRead(package);
            var satellites = archive.Entries
                .Select(static entry => entry.FullName)
                .Where(static name => name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(satellites.Count == 0, "The package carries satellite assemblies: " + string.Join(", ", satellites));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    /// <summary>The culture a resource file is written for, from its name: <c>Messages.&lt;culture&gt;.resx</c>.</summary>
    private static string? CultureOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return null;
        }

        var candidate = name[(dot + 1)..];
        try
        {
            return CultureInfo.GetCultureInfo(candidate, predefinedOnly: true).Name.Length > 0 ? candidate : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Latin letters in every block Unicode gives them, phonetic and letter-like symbols included, so that
    /// typography such as a ligature or a degree sign never counts as another script.
    /// </summary>
    private static bool IsLatin(int codePoint) =>
        codePoint <= 0x02FF
        || codePoint is >= 0x1D00 and <= 0x1DBF
        || codePoint is >= 0x1E00 and <= 0x1EFF
        || codePoint is >= 0x2070 and <= 0x209F
        || codePoint is >= 0x2100 and <= 0x214F
        || codePoint is >= 0x2C60 and <= 0x2C7F
        || codePoint is >= 0xA720 and <= 0xA7FF
        || codePoint is >= 0xAB30 and <= 0xAB6F
        || codePoint is >= 0xFB00 and <= 0xFB06
        || codePoint is >= 0xFF21 and <= 0xFF3A
        || codePoint is >= 0xFF41 and <= 0xFF5A;

    private static IEnumerable<string> Files(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var pending = new Stack<string>([directory]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.Ordinal))
                {
                    pending.Push(child);
                }
            }
        }
    }

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

    private static async Task<(int ExitCode, string Output)> RunAsync(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "true";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await standardOutput + await standardError);
    }
}
