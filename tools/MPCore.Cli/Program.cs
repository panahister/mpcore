using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

return await MPCoreCli.RunAsync(args);

internal static partial class MPCoreCli
{
    // The CLI, the template package and the runtime packages ship as one immutable cohort. A
    // published version is never rebuilt with different bytes: a corrected build always takes the
    // next unused prerelease version, because a mutable version identity silently mixes a stale
    // template with current runtime packages and produces a consumer that cannot compile.
    internal const string CohortVersionValue = "0.9.4";

    private const string DefaultMPCoreVersion = CohortVersionValue;

    // Written by the template package and read back immediately after generation. Its only purpose
    // is to make a CLI/template version mismatch a loud failure instead of wrong generated source.
    internal const string TemplateVersionMarkerFileName = ".mpcore-template-version";

    // Version 4 keeps every version 3 member and adds the AI tooling selection, because the generated
    // assistant instructions and skills are part of the repository's shape. Tooling must treat an
    // unrecognized schema version as incompatible and refuse to act rather than guessing.
    private const int ManifestSchemaVersion = 4;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length >= 1 && args[0] == "configure")
        {
            return await Configure.RunAsync(ParseOptions(args[1..]));
        }

        if (args.Length < 2 || args[0] != "new" || args[1] != "backend")
        {
            PrintUsage();
            Console.WriteLine();
            Configure.PrintUsage();
            return 2;
        }

        var options = ParseOptions(args[2..]);
        if (options.ContainsKey("list-presets"))
        {
            PrintPresets();
            return 0;
        }

        if (options.TryGetValue("preset", out var presetName))
        {
            if (!Presets.TryGetValue(presetName, out var preset))
            {
                Console.Error.WriteLine($"Unknown preset '{presetName}'.");
                PrintPresets();
                return 2;
            }

            // A preset fills in what the caller did not say; an explicit flag always wins.
            foreach (var (key, value) in preset)
            {
                options.TryAdd(key, value);
            }
        }

        // Interactive when asked for, or when a person at a terminal left required options out.
        // Never when stdin is a pipe or --non-interactive is set: a build agent must fail, not hang.
        var wizard = options.ContainsKey("interactive")
                     || (!options.ContainsKey("non-interactive") && !Console.IsInputRedirected && MissingRequired(options));
        if (wizard && !RunWizard(options))
        {
            return 2;
        }

        if (!TryGetRequired(options, "organization", out var organization) ||
            !TryGetRequired(options, "component", out var component) ||
            !TryGetRequired(options, "output", out var output))
        {
            PrintUsage();
            return 2;
        }

        if (!IdentifierPattern().IsMatch(organization) || !IdentifierPattern().IsMatch(component))
        {
            Console.Error.WriteLine("Organization and component must be PascalCase .NET identifiers.");
            return 2;
        }

        var shape = options.GetValueOrDefault("shape", "service");
        var messaging = options.GetValueOrDefault("messaging", "kafka");
        // Defaults to none: an audit trail is a business obligation that must be chosen, and choosing
        // it adds a table, a package and a policy file the team has to own.
        var businessAudit = options.GetValueOrDefault("business-audit", "none");
        // Defaults to memory, the only choice with no external dependency: a cache needing a server the
        // team has not provisioned would fail at startup, so anything else must be asked for.
        var cache = options.GetValueOrDefault("cache", "memory");
        // Defaults to none: TimescaleDB is an extension the database must have, not a library choice.
        var timeseries = options.GetValueOrDefault("timeseries", "none");
        var mpcoreVersion = options.GetValueOrDefault("mpcore-version", DefaultMPCoreVersion);

        // Defaults to both so a generated repository is usable from either assistant without a flag.
        // Unlike --transport this has a default: it changes no runtime behaviour and no contract, so
        // an unconsidered value cannot produce a security or protocol surprise.
        var aiTooling = options.GetValueOrDefault("ai-tooling", "both");
        if (options.ContainsKey("ai-tooling") && string.IsNullOrEmpty(aiTooling))
        {
            Console.Error.WriteLine("--ai-tooling was given without a value.");
            return 2;
        }

        // --transport is required: there is no default and no inference from --shape or --messaging,
        // because the protocol surface is a security- and contract-relevant decision.
        if (!options.TryGetValue("transport", out var transport) || string.IsNullOrWhiteSpace(transport))
        {
            Console.Error.WriteLine("Missing required option --transport.");
            PrintUsage();
            return 2;
        }

        if (!ExactSemanticVersionPattern().IsMatch(mpcoreVersion))
        {
            Console.Error.WriteLine(
                "--mpcore-version must be an exact semantic version such as 0.9.0 or 1.0.0-rc.1; ranges and wildcards are prohibited.");
            return 2;
        }

        if (shape is not ("service" or "modular-monolith"))
        {
            Console.Error.WriteLine("--shape must be service or modular-monolith.");
            return 2;
        }

        if (messaging is not ("kafka" or "rabbitmq" or "none"))
        {
            Console.Error.WriteLine("--messaging must be kafka, rabbitmq, or none.");
            return 2;
        }

        if (cache is not ("none" or "memory" or "redis" or "hybrid"))
        {
            Console.Error.WriteLine("--cache must be none, memory, redis, or hybrid.");
            return 2;
        }

        if (timeseries is not ("none" or "timescale"))
        {
            Console.Error.WriteLine("--timeseries must be none or timescale.");
            return 2;
        }

        if (businessAudit is not ("none" or "postgresql"))
        {
            Console.Error.WriteLine("--business-audit must be none or postgresql.");
            return 2;
        }

        if (transport is not ("grpc" or "rest" or "both"))
        {
            Console.Error.WriteLine("--transport must be grpc, rest, or both.");
            return 2;
        }

        if (aiTooling is not ("both" or "codex" or "claude" or "none"))
        {
            Console.Error.WriteLine("--ai-tooling must be both, codex, claude, or none.");
            return 2;
        }

        // Non-secret connection values only. A password, API key or client secret must never reach a
        // command line, a manifest or a tracked file: those are user secrets or environment variables.
        foreach (var option in new[] { "security-authority", "security-audience" })
        {
            if (options.TryGetValue(option, out var candidate) && LooksLikeSecret(candidate))
            {
                Console.Error.WriteLine(
                    $"--{option} looks like a credential. Pass only non-secret values here and set "
                    + "anything sensitive with user secrets or environment variables.");
                return 2;
            }
        }

        // A generated repository targets net10.0. Finding that out from a compile error after a
        // successful "created" message wastes the developer's time; the check costs one process.
        var sdk = await EffectiveSdkAsync(output);
        if (sdk.Problem is not null)
        {
            Console.Error.WriteLine(sdk.Problem);
            return 6;
        }

        // Every validation above runs before any directory is created or any template is invoked.
        var target = Path.GetFullPath(output);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            Console.Error.WriteLine($"Output directory is not empty: {target}");
            return 3;
        }

        var productName = $"{organization}.{component}";
        Directory.CreateDirectory(target);

        // Captured immediately after the emptiness gate above so the cleanup path can re-assert the
        // invariant itself instead of trusting its call sites.
        var preExistingEntries = Directory.GetFileSystemEntries(target);

        var templateExitCode = await RunProcessAsync(
            "dotnet",
            [
                "new", "mpcore-backend",
                "--name", productName,
                "--output", target,
                "--organization", organization,
                "--component", component,
                "--shape", shape,
                "--messaging", messaging,
                "--transport", transport,
                "--businessAudit", businessAudit,
                "--cache", cache,
                "--timeseries", timeseries,
                "--aiTooling", aiTooling,
                "--securityAuthority", options.GetValueOrDefault("security-authority", "https://identity.invalid/realms/replace-me"),
                "--securityAudience", options.GetValueOrDefault("security-audience", "replace-me"),
                "--mpcoreVersion", mpcoreVersion
            ],
            Directory.GetCurrentDirectory());

        if (templateExitCode != 0)
        {
            Console.Error.WriteLine(
                "Template generation failed. Install the matching MPCore.Templates package from the approved feed first.");
            return templateExitCode;
        }

        // A template package whose version differs from this CLI generates source against a
        // different MP Core API surface. `dotnet new install` is a no-op when the same version is
        // already installed, so a stale template survives an apparently successful reinstall and the
        // mismatch only surfaces as a compile error in the generated repository. Fail here instead.
        var markerPath = Path.Combine(target, TemplateVersionMarkerFileName);
        var reinstall =
            $"dotnet new uninstall MPCore.Templates && dotnet new install MPCore.Templates::{CohortVersionValue}";

        if (!File.Exists(markerPath))
        {
            DiscardGeneratedOutput(target, preExistingEntries);
            Console.Error.WriteLine(
                $"The installed mpcore-backend template does not carry a {TemplateVersionMarkerFileName} " +
                $"marker, so it predates CLI {CohortVersionValue} and cannot be verified. Reinstall the " +
                $"matching template: {reinstall}");
            return 4;
        }

        var templateVersion = (await File.ReadAllTextAsync(markerPath)).Trim();
        if (!string.Equals(templateVersion, CohortVersionValue, StringComparison.Ordinal))
        {
            DiscardGeneratedOutput(target, preExistingEntries);
            Console.Error.WriteLine(
                $"Template/CLI version mismatch: the installed mpcore-backend template is " +
                $"{templateVersion} but this CLI is {CohortVersionValue}. Install the matching template: " +
                $"{reinstall}");
            return 4;
        }

        File.Delete(markerPath);

        var manifestDirectory = Path.Combine(target, ".mpcore");
        Directory.CreateDirectory(manifestDirectory);
        var manifest = new
        {
            schemaVersion = ManifestSchemaVersion,
            organization,
            component,
            productName,
            shape,
            messaging,
            transport,
            businessAudit,
            cache,
            timeseries,
            aiTooling,
            mpcoreVersion,
            templateVersion,
            cliVersion = CohortVersionValue,
            generatedAtUtc = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(
            Path.Combine(manifestDirectory, "template-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        var solutionName = $"{productName}.Backend";
        var solutionExitCode = await RunProcessAsync(
            "dotnet",
            ["new", "sln", "--name", solutionName, "--format", "sln"],
            target);
        if (solutionExitCode != 0)
        {
            return solutionExitCode;
        }

        var solutionPath = Path.Combine(target, $"{solutionName}.sln");
        var projects = Directory.GetFiles(target, "*.csproj", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var addArguments = new List<string> { "sln", solutionPath, "add" };
        addArguments.AddRange(projects);
        var addExitCode = await RunProcessAsync("dotnet", addArguments, target);
        if (addExitCode != 0)
        {
            return addExitCode;
        }

        if (!options.ContainsKey("skip-verify"))
        {
            var restoreExitCode = await RunProcessAsync("dotnet", ["restore", solutionPath], target);
            if (restoreExitCode != 0)
            {
                return restoreExitCode;
            }

            var buildExitCode = await RunProcessAsync(
                "dotnet",
                ["build", solutionPath, "--configuration", "Release", "--no-restore"],
                target);
            if (buildExitCode != 0)
            {
                return buildExitCode;
            }
        }

        // A path the developer can act on immediately. Only files that were actually generated are
        // named: claiming a skills guide exists under --ai-tooling none would be a broken promise.
        Console.WriteLine();
        Console.WriteLine($"Created {productName} at {target}");
        Console.WriteLine();
        Console.WriteLine($"  Read me first  {Path.Combine(target, "README.md")}");
        Console.WriteLine($"  Quick start    {Path.Combine(target, "docs", "getting-started.md")}");
        Console.WriteLine($"  Architecture   {Path.Combine(target, "docs", "architecture.md")}");
        Console.WriteLine($"  Capabilities   {Path.Combine(target, "docs", "capabilities.md")}");
        Console.WriteLine($"  Workflow       {Path.Combine(target, "docs", "development-workflow.md")}");
        if (aiTooling != "none")
        {
            Console.WriteLine($"  AI skills      {Path.Combine(target, "docs", "ai-skills.md")}");
        }

        Console.WriteLine();
        Console.WriteLine("Next:");
        Console.WriteLine("  1. Replace Security:Authority, Security:Audiences and ConnectionStrings:PostgreSql");
        Console.WriteLine("     before running the host. Use user secrets or environment variables, not appsettings.json.");
        Console.WriteLine(transport switch
        {
            "rest" => "  2. Run the host and check http://localhost:8080/health/live",
            "grpc" => "  2. Run the host; gRPC listens on :8081 over HTTP/2 with grpc.health.v1.Health",
            _ => "  2. Run the host; REST is on :8080 and gRPC on :8081, one port each",
        });
        if (messaging == "none")
        {
            Console.WriteLine("  3. No external broker is configured. Wolverine local queues are available for");
            Console.WriteLine("     in-process work; adding Kafka or RabbitMQ is a scope change.");
        }
        else
        {
            Console.WriteLine($"  3. Configure the {messaging} connection before using messaging. Never commit a credential.");
        }

        if (timeseries == "timescale")
        {
            Console.WriteLine("     TimescaleDB helpers are referenced; the database must have the timescaledb extension. Add");
            Console.WriteLine("     hypertables in a migration with migrationBuilder.CreateHypertable(...); see docs/getting-started.md.");
        }

        if (cache is "redis" or "hybrid")
        {
            Console.WriteLine($"     The {cache} cache needs ConnectionStrings:Redis before the host starts. Put a password");
            Console.WriteLine("     in user secrets or the environment, never in appsettings.json.");
        }

        if (businessAudit == "postgresql")
        {
            Console.WriteLine("     Business audit writes to schema audit, table entries: add an EF migration, grant the");
            Console.WriteLine("     runtime role INSERT and SELECT only, and declare audited entities in");
            Console.WriteLine("     src/*.Infrastructure/Audit/AuditPolicyConfiguration.cs. Nothing is recorded until you do.");
        }

        Console.WriteLine(aiTooling == "none"
            ? "  4. Bring an approved requirement with acceptance criteria, then follow docs/development-workflow.md."
            : "  4. Bring an approved requirement with acceptance criteria, then open docs/ai-skills.md and pick a skill.");
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Removes what this run generated so a corrected retry is not blocked by the non-empty output
    /// check.
    /// </summary>
    /// <param name="target">The generation output directory.</param>
    /// <param name="preExistingEntries">
    /// Entries present after the emptiness gate and before generation. The method refuses to delete
    /// anything unless this is empty, so it cannot be repurposed into an unguarded recursive delete
    /// of a user-supplied path by a later change.
    /// </param>
    private static void DiscardGeneratedOutput(string target, string[] preExistingEntries)
    {
        if (preExistingEntries.Length != 0)
        {
            Console.Error.WriteLine(
                $"Refusing to clean {target}: it was not empty before generation. Remove the " +
                "generated output manually before retrying.");
            return;
        }

        try
        {
            // Materialized before deleting: removing entries while a lazy directory enumerator is
            // open has unspecified visibility for the remaining entries, so a lazy walk can silently
            // skip files and leave the directory non-empty without raising anything.
            foreach (var directory in Directory.GetDirectories(target))
            {
                Directory.Delete(directory, recursive: true);
            }

            foreach (var file in Directory.GetFiles(target))
            {
                File.Delete(file);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not clean {target}: {exception.Message}");
        }

        // Post-condition: the retry this message promises only works if the directory really is
        // empty, so an incomplete clean must be reported rather than assumed.
        if (Directory.EnumerateFileSystemEntries(target).Any())
        {
            Console.Error.WriteLine(
                $"{target} still contains generated files. Delete it manually before retrying.");
        }
    }

    private static bool LooksLikeSecret(string value) =>
        value.Contains("password", StringComparison.OrdinalIgnoreCase)
        || value.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || value.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || value.Contains("api-key", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the SDK that will actually be used at the output path, not merely the ones installed.
    /// A global.json anywhere above the output directory can pin a different SDK, so "10.x appears in
    /// --list-sdks" is not the question; "which SDK does this directory resolve to" is. `dotnet
    /// --version` answers exactly that, and fails when the pinned SDK is absent.
    /// </summary>
    private static async Task<(string? Version, string? Problem)> EffectiveSdkAsync(string outputPath)
    {
        // The output directory may not exist yet; resolution follows the nearest existing ancestor,
        // which is where a global.json would be found.
        var probe = Path.GetFullPath(outputPath);
        while (!Directory.Exists(probe))
        {
            var parent = Path.GetDirectoryName(probe);
            if (parent is null || parent == probe)
            {
                probe = Directory.GetCurrentDirectory();
                break;
            }

            probe = parent;
        }

        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = probe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--version");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (null, null);
            }

            var stdout = (await process.StandardOutput.ReadToEndAsync()).Trim();
            var stderr = (await process.StandardError.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 || stdout.Length == 0)
            {
                var pinned = FindGlobalJson(probe);
                return (null,
                    "The .NET SDK could not be resolved for this location"
                    + (pinned is null ? "." : $", which is governed by {pinned}.")
                    + Environment.NewLine
                    + (string.IsNullOrEmpty(stderr) ? string.Empty : stderr + Environment.NewLine)
                    + "A generated repository targets net10.0. Install the pinned SDK, or adjust global.json.");
            }

            if (!stdout.StartsWith("10.", StringComparison.Ordinal))
            {
                var pinned = FindGlobalJson(probe);
                return (stdout,
                    $"The SDK selected at this location is {stdout}, but a generated repository targets net10.0."
                    + (pinned is null
                        ? " Install a .NET 10 SDK."
                        : $" {pinned} pins it; change that file or generate elsewhere.")
                    + Environment.NewLine
                    + "Installed SDKs are not the question: this is the one this directory resolves to.");
            }

            return (stdout, null);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // If the SDK cannot be interrogated at all, do not block on a check that failed for an
            // unrelated reason; the build will report the real problem.
            return (null, null);
        }
    }

    private static string? FindGlobalJson(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "global.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tokens = args.ToArray();
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = token[2..];
            if (key is "skip-verify" or "interactive" or "non-interactive" or "list-presets")
            {
                result[key] = "true";
                continue;
            }

            if (index + 1 < tokens.Length && !tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = tokens[++index];
            }
            else
            {
                // Silently dropping it would apply a default the caller did not ask for, which is how
                // "--ai-tooling" with a forgotten value produced a fully AI-enabled repository.
                result[key] = string.Empty;
            }
        }

        return result;
    }

    private static bool TryGetRequired(
        IReadOnlyDictionary<string, string> options,
        string key,
        out string value)
    {
        if (options.TryGetValue(key, out var candidate) && !string.IsNullOrWhiteSpace(candidate))
        {
            value = candidate;
            return true;
        }

        Console.Error.WriteLine($"Missing required option --{key}.");
        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Runs a child process without letting its output into ours. The template writes its own success
    /// message, which is noise inside a command that reports its own plan.
    /// </summary>
    internal static async Task<int> RunProcessQuietAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);

        if (process.ExitCode != 0)
        {
            // On failure the child's diagnosis is the useful part, so it is surfaced then and only then.
            Console.Error.WriteLine(await stderr);
        }

        return process.ExitCode;
    }

    internal static async Task<int> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    internal static bool IsExactSemanticVersion(string value) => ExactSemanticVersionPattern().IsMatch(value);

    private static void PrintUsage() => Console.WriteLine(
        "mpcore new backend --organization Acme --component Catalog --output ./Catalog " +
        "--transport grpc|rest|both " +
        "[--shape service|modular-monolith] [--messaging kafka|rabbitmq|none] " +
        "[--business-audit none|postgresql] [--cache none|memory|redis|hybrid] [--timeseries none|timescale] " +
        "[--ai-tooling both|codex|claude|none] " +
        "[--security-authority <url>] [--security-audience <audience>] " +
        $"[--mpcore-version {DefaultMPCoreVersion}] [--skip-verify] " +
        "[--preset api|service|modular-monolith] [--interactive | --non-interactive] [--list-presets]");

    // Presets are starting points a person can read in one line, not hidden defaults: every value is
    // printed by --list-presets, and any explicit flag overrides its preset value.
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Presets =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["api"] = new Dictionary<string, string>
            {
                ["transport"] = "rest", ["shape"] = "service", ["messaging"] = "none",
                ["cache"] = "memory", ["business-audit"] = "none",
            },
            ["service"] = new Dictionary<string, string>
            {
                ["transport"] = "both", ["shape"] = "service", ["messaging"] = "kafka",
                ["cache"] = "redis", ["business-audit"] = "postgresql",
            },
            ["modular-monolith"] = new Dictionary<string, string>
            {
                ["transport"] = "rest", ["shape"] = "modular-monolith", ["messaging"] = "rabbitmq",
                ["cache"] = "hybrid", ["business-audit"] = "postgresql",
            },
        };

    private static void PrintPresets()
    {
        Console.WriteLine("Presets (explicit flags override any preset value):");
        foreach (var (name, values) in Presets.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {name,-17} " + string.Join(" ", values.Select(v => $"--{v.Key} {v.Value}")));
        }
    }

    private static bool MissingRequired(IReadOnlyDictionary<string, string> options) =>
        new[] { "organization", "component", "output", "transport" }
            .Any(key => !options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value));

    /// <summary>
    /// Asks for what is missing and confirms the whole selection. Every answer lands in the same
    /// options the flags would have set, so the rest of the command cannot tell the two apart.
    /// </summary>
    private static bool RunWizard(Dictionary<string, string> options)
    {
        Console.WriteLine("MP Core backend — answer each question or press Enter for the value in brackets.");
        Console.WriteLine("Nothing here is a secret: passwords and API keys are set later with user secrets.");
        Console.WriteLine();

        if (!Ask(options, "organization", "Organization (PascalCase)", null, v => IdentifierPattern().IsMatch(v), "must be a PascalCase .NET identifier")) return false;
        if (!Ask(options, "component", "Component (PascalCase)", null, v => IdentifierPattern().IsMatch(v), "must be a PascalCase .NET identifier")) return false;
        if (!Ask(options, "output", "Output directory", options.TryGetValue("component", out var c) ? $"./{c}" : null, v => !string.IsNullOrWhiteSpace(v), "is required")) return false;
        if (!Choose(options, "transport", "Transport", ["grpc", "rest", "both"], null)) return false;
        if (!Choose(options, "shape", "Shape", ["service", "modular-monolith"], "service")) return false;
        if (!Choose(options, "messaging", "Messaging", ["kafka", "rabbitmq", "none"], "kafka")) return false;
        if (!Choose(options, "cache", "Cache", ["none", "memory", "redis", "hybrid"], "memory")) return false;
        if (!Choose(options, "business-audit", "Business audit", ["none", "postgresql"], "none")) return false;
        if (!Choose(options, "timeseries", "Time series (TimescaleDB helpers)", ["none", "timescale"], "none")) return false;
        if (!Choose(options, "ai-tooling", "AI tooling", ["both", "codex", "claude", "none"], "both")) return false;
        if (!Ask(options, "security-authority", "OIDC authority URL (non-secret; placeholder allowed)", "https://identity.invalid/realms/replace-me", v => Uri.TryCreate(v, UriKind.Absolute, out _) && !LooksLikeSecret(v), "must be an absolute URL and not a credential")) return false;
        if (!Ask(options, "security-audience", "Token audience (non-secret)", "replace-me", v => !string.IsNullOrWhiteSpace(v) && !LooksLikeSecret(v), "is required and must not be a credential")) return false;

        Console.WriteLine();
        Console.WriteLine("Selection:");
        foreach (var key in new[] { "organization", "component", "output", "transport", "shape", "messaging", "cache", "business-audit", "timeseries", "ai-tooling", "security-authority", "security-audience" })
        {
            Console.WriteLine($"  --{key,-18} {options[key]}");
        }

        Console.Write("Proceed? [Y/n] ");
        var answer = Console.ReadLine()?.Trim();
        if (answer is not (null or "" or "y" or "Y" or "yes"))
        {
            Console.WriteLine("Cancelled. Nothing was created.");
            return false;
        }

        return true;
    }

    private static bool Ask(Dictionary<string, string> options, string key, string label, string? fallback, Func<string, bool> valid, string rule)
    {
        var current = options.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing) ? existing : fallback;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Console.Write(current is null ? $"{label}: " : $"{label} [{current}]: ");
            var line = Console.ReadLine();
            if (line is null)
            {
                Console.Error.WriteLine("Input ended. Nothing was created.");
                return false;
            }

            var value = string.IsNullOrWhiteSpace(line) ? current : line.Trim();
            if (value is not null && valid(value))
            {
                options[key] = value;
                return true;
            }

            Console.WriteLine($"  {label} {rule}.");
        }

        Console.Error.WriteLine("Too many invalid answers. Nothing was created.");
        return false;
    }

    private static bool Choose(Dictionary<string, string> options, string key, string label, string[] choices, string? fallback) =>
        Ask(options, key, $"{label} ({string.Join("|", choices)})", fallback, v => choices.Contains(v, StringComparer.Ordinal), $"must be one of {string.Join(", ", choices)}");

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExactSemanticVersionPattern();
}
