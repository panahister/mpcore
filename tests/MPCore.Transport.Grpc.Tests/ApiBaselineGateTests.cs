using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace MPCore.Transport.Grpc.Tests;

/// <summary>
/// The API of every runtime package is validated against the last published version, and a build fails when that
/// package is missing or was not verified (ADR-010, addendum of 2026-10-12). Each test packs a mirror of
/// <c>MPCore.Domain</c>, which depends on nothing, with this repository's build files and the verified baseline
/// that <c>eng/restore-api-baseline.sh</c> restored, and plants one fault. They pack inside the test, restoring
/// from the network, so they run in the slow job of CI and in the release gate.
/// </summary>
[Trait("Category", "Packaging")]
public sealed partial class ApiBaselineGateTests : IDisposable
{
    private const string Project = "MPCore.Domain";

    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static readonly string Baseline = BaselineVersion();

    private readonly string _mirror = Directory.CreateTempSubdirectory("mpcore-baseline-gate-").FullName;

    public ApiBaselineGateTests()
    {
        foreach (var file in new[] { "Directory.Build.props", "Directory.Packages.props", "global.json", "NuGet.config", "src/Directory.Build.props", "docs/nuget/README.md" })
        {
            Copy(Path.Combine(RepositoryRoot, file), Path.Combine(_mirror, file));
        }

        CopyDirectory(Path.Combine(RepositoryRoot, "src", Project), Path.Combine(_mirror, "src", Project));
        foreach (var name in new[] { PackageName, PackageName + ".verified" })
        {
            var source = Path.Combine(RepositoryRoot, "artifacts/packages", name);
            Assert.True(File.Exists(source), $"{source} is missing: run ./eng/restore-api-baseline.sh, as CI does before this test.");
            Copy(source, Path.Combine(_mirror, "artifacts/packages", name));
        }
    }

    private static string PackageName => $"{Project}.{Baseline}.nupkg";

    private string Package => Path.Combine(_mirror, "artifacts/packages", PackageName);

    [Fact]
    public async Task A_package_that_keeps_the_public_api_is_validated_against_the_baseline_and_packs()
    {
        var (exitCode, log) = await PackAsync();

        Assert.True(exitCode == 0, log);
        Assert.Contains($"Successfully created package", log, StringComparison.Ordinal);
        Assert.DoesNotContain("SKIPPED", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_changed_public_member_fails_the_build_and_the_pack_against_the_baseline()
    {
        // A public property of the baseline made internal: the break the validation exists to catch.
        var file = Path.Combine(_mirror, "src", Project, "Rules/BusinessRules.cs");
        var source = File.ReadAllText(file);
        const string declaration = "public static IReadOnlyDictionary<string, string> NoArguments";
        Assert.Contains(declaration, source, StringComparison.Ordinal);
        File.WriteAllText(file, source.Replace(declaration, "internal static IReadOnlyDictionary<string, string> NoArguments", StringComparison.Ordinal));

        // Two guards, each alone: the analyzer fails the build because the inventory lists a member that is gone
        // (RS0017), and package validation fails the pack because the baseline had it (CP0002).
        var (analyzerExit, analyzerLog) = await PackAsync();
        var (validationExit, validationLog) = await PackAsync(onCi: false, "-p:RunAnalyzers=false");

        Assert.NotEqual(0, analyzerExit);
        Assert.Contains("RS0017", analyzerLog, StringComparison.Ordinal);
        Assert.NotEqual(0, validationExit);
        Assert.Contains("CP0002", validationLog, StringComparison.Ordinal);
        Assert.Contains("NoArguments", validationLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_baseline_fails_the_build_and_names_how_to_restore_it()
    {
        File.Delete(Package);

        var (exitCode, log) = await PackAsync();

        Assert.NotEqual(0, exitCode);
        Assert.Contains("MPCORE0005", log, StringComparison.Ordinal);
        Assert.Contains("./eng/restore-api-baseline.sh", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_baseline_that_was_never_verified_fails_the_build()
    {
        File.Delete(Package + ".verified");

        var (exitCode, log) = await PackAsync();

        Assert.NotEqual(0, exitCode);
        Assert.Contains("MPCORE0006", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_baseline_package_re_zipped_without_its_signature_fails_the_build()
    {
        // The package nuget.org signed, rewritten without the signature: the same entries, other bytes.
        var rewritten = Package + ".rewritten";
        using (var original = ZipFile.OpenRead(Package))
        using (var target = ZipFile.Open(rewritten, ZipArchiveMode.Create))
        {
            Assert.Contains(original.Entries, static entry => entry.FullName == ".signature.p7s");
            foreach (var entry in original.Entries.Where(static entry => entry.FullName != ".signature.p7s"))
            {
                var copy = target.CreateEntry(entry.FullName);
                using var from = entry.Open();
                using var to = copy.Open();
                from.CopyTo(to);
            }
        }

        File.Move(rewritten, Package, overwrite: true);

        var (exitCode, log) = await PackAsync();

        Assert.NotEqual(0, exitCode);
        Assert.Contains("MPCORE0007", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_baseline_package_replaced_after_it_was_verified_fails_the_build()
    {
        await File.AppendAllTextAsync(Package, "x");

        var (exitCode, log) = await PackAsync();

        Assert.NotEqual(0, exitCode);
        Assert.Contains("MPCORE0007", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Building_without_a_baseline_needs_an_explicit_switch_that_warns_and_that_ci_refuses()
    {
        File.Delete(Package);

        var (offlineExit, offlineLog) = await PackAsync(onCi: false, "-p:MPCoreOfflineWithoutApiBaseline=true");
        var (ciExit, ciLog) = await PackAsync(onCi: true, "-p:MPCoreOfflineWithoutApiBaseline=true");

        Assert.True(offlineExit == 0, offlineLog);
        Assert.Contains("MPCORE0002", offlineLog, StringComparison.Ordinal);
        Assert.NotEqual(0, ciExit);
        Assert.Contains("MPCORE0001", ciLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_package_listed_as_new_in_the_cohort_fails_the_build_while_a_baseline_exists_or_its_absence_is_unproven()
    {
        var (existsExit, existsLog) = await PackAsync(onCi: false, $"-p:MPCoreNewInCohort={Project}");
        Assert.NotEqual(0, existsExit);
        Assert.Contains("MPCORE0003", existsLog, StringComparison.Ordinal);

        File.Delete(Package);
        var (unprovenExit, unprovenLog) = await PackAsync(onCi: false, $"-p:MPCoreNewInCohort={Project}");
        Assert.NotEqual(0, unprovenExit);
        Assert.Contains("MPCORE0004", unprovenLog, StringComparison.Ordinal);

        // With the proof the script writes when nuget.org has no baseline, the package is built, not validated.
        File.WriteAllText(Path.Combine(_mirror, "artifacts/packages", $"{Project}.{Baseline}.new"), "nuget.org has no baseline\n");
        var (newExit, newLog) = await PackAsync(onCi: false, $"-p:MPCoreNewInCohort={Project}");
        Assert.True(newExit == 0, newLog);
    }

    public void Dispose() => Directory.Delete(_mirror, recursive: true);

    private async Task<(int ExitCode, string Log)> PackAsync(bool onCi = false, params string[] extra)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _mirror,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "pack", $"src/{Project}/{Project}.csproj", "--configuration", "Release", "--output", Path.Combine(_mirror, "out"), "-nodeReuse:false", "-nologo" }.Concat(extra))
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "true";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        // CI machines set these; a run of the tests on one still has to see the build as a person's.
        start.Environment.Remove("CI");
        start.Environment.Remove("GITHUB_ACTIONS");
        if (onCi)
        {
            start.Environment["CI"] = "true";
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
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

    private static string BaselineVersion()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot, "src/Directory.Build.props"));
        return BaselinePattern().Match(props).Groups[1].Value;
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            if (relative.Split(Path.DirectorySeparatorChar).Any(static part => part is "bin" or "obj"))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(static part => part is "bin" or "obj"))
            {
                continue;
            }

            File.Copy(file, Path.Combine(target, relative));
        }
    }

    [GeneratedRegex(@"<MPCoreBaselineVersion>([^<]+)</MPCoreBaselineVersion>")]
    private static partial Regex BaselinePattern();
}
