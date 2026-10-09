using System.Text.Json;
using System.Text.RegularExpressions;

namespace MPCore.Transport.Grpc.Tests;

public sealed class TemplateContractTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    private static readonly string TemplateRoot =
        Path.Combine(RepositoryRoot, "tools/MPCore.Templates/content/MPCore.Backend");

    /// <summary>
    /// The single version the CLI, the template package and the runtime packages ship under. A
    /// published version is never rebuilt with different bytes, so this constant moves only when a
    /// new prerelease is cut.
    /// </summary>
    private const string CohortVersion = "0.10.0";

    [Fact]
    public void The_template_targets_the_current_cohort_and_the_transport_neutral_host()
    {
        using var definition = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(TemplateRoot, ".template.config/template.json")));
        var symbols = definition.RootElement.GetProperty("symbols");

        Assert.Equal(CohortVersion, symbols.GetProperty("mpcoreVersion").GetProperty("defaultValue").GetString());
        Assert.True(Directory.Exists(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api")));
        Assert.False(Directory.Exists(Path.Combine(TemplateRoot, "src/MPCore.Backend.GrpcApi")));
    }

    [Fact]
    public void The_transport_symbol_is_a_required_choice_with_computed_inclusion_flags()
    {
        using var definition = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(TemplateRoot, ".template.config/template.json")));
        var symbols = definition.RootElement.GetProperty("symbols");
        var transport = symbols.GetProperty("transport");

        Assert.Equal("choice", transport.GetProperty("datatype").GetString());
        Assert.True(transport.GetProperty("isRequired").GetBoolean());
        Assert.False(transport.TryGetProperty("defaultValue", out _));
        Assert.Equal(
            new[] { "grpc", "rest", "both" },
            transport.GetProperty("choices").EnumerateArray()
                .Select(choice => choice.GetProperty("choice").GetString()));
        Assert.Equal(
            "(transport == \"grpc\" || transport == \"both\")",
            symbols.GetProperty("includeGrpc").GetProperty("value").GetString());
        Assert.Equal(
            "(transport == \"rest\" || transport == \"both\")",
            symbols.GetProperty("includeRest").GetProperty("value").GetString());
    }

    [Fact]
    public void Transport_specific_content_is_excluded_by_source_modifiers()
    {
        using var definition = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(TemplateRoot, ".template.config/template.json")));
        var modifiers = definition.RootElement
            .GetProperty("sources").EnumerateArray().Single()
            .GetProperty("modifiers").EnumerateArray()
            .ToDictionary(
                modifier => modifier.GetProperty("condition").GetString()!,
                modifier => modifier.GetProperty("exclude").EnumerateArray()
                    .Select(path => path.GetString()!)
                    .ToArray(),
                StringComparer.Ordinal);

        Assert.Equal(
            new[] { "src/MPCore.Backend.Api/Protos/**", "src/MPCore.Backend.Api/Grpc/**" },
            modifiers["(!includeGrpc)"]);
        Assert.Equal(new[] { "src/MPCore.Backend.Api/Rest/**" }, modifiers["(!includeRest)"]);
        // A service has no modules, and so no module rules to test.
        Assert.Equal(new[] { "src/Modules/**", "tests/MPCore.Backend.Tests/ModuleRulesTests.cs" }, modifiers["(shape == \"service\")"]);
        // A modular monolith is its modules: one project per bounded context. The root Domain and
        // Application projects would be empty there, so they are not generated at all.
        Assert.Equal(
            new[] { "src/MPCore.Backend.Domain/**", "src/MPCore.Backend.Application/**" },
            modifiers["(shape == \"modular-monolith\")"]);

        // Port separation only exists where two listeners exist; without this assertion the
        // exclusion could be dropped and single-transport hosts would ship dead middleware.
        Assert.Equal(
            new[] { "src/MPCore.Backend.Api/Hosting/TransportPortSeparation.cs" },
            modifiers["(transport != \"both\")"]);
        // Asserting the exact pairing, not a count. Swapping these two conditions makes
        // `--ai-tooling codex` emit a Claude-only repository, and a bare count cannot see it.
        Assert.Equal(new[] { "AGENTS.md", ".agents/**" }, modifiers["(!includeCodex)"]);
        Assert.Equal(new[] { "CLAUDE.md", ".claude/**" }, modifiers["(!includeClaude)"]);
        Assert.Equal(new[] { ".mpcore/skills/**", "docs/ai-skills.md" }, modifiers["(!includeAiSkills)"]);
        // The policy file is the only audit-specific file; the rest is conditional inside shared files.
        Assert.Equal(
            new[] { "src/MPCore.Backend.Infrastructure/Audit/**" },
            modifiers["(!includeBusinessAudit)"]);
        // A modular monolith has no module yet to hold an architecture test against: the file is
        // generated for shape == "service" only, where the domain and the application are fixed projects.
        Assert.Equal(
            new[] { "tests/MPCore.Backend.Tests/ArchitectureTests.cs" },
            modifiers["(shape != \"service\")"]);
        Assert.Equal(10, modifiers.Count);  // conditions are unique; duplicates would mask a rule
    }

    [Fact]
    public void The_host_composes_security_and_both_transport_adapters_conditionally()
    {
        var project = File.ReadAllText(
            Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/MPCore.Backend.Api.csproj"));
        var program = File.ReadAllText(
            Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));

        Assert.Contains("MPCore.Security.Abstractions", project, StringComparison.Ordinal);
        Assert.Contains("MPCore.Security.AspNetCore", project, StringComparison.Ordinal);
        Assert.Contains("<!--#if (includeGrpc) -->", project, StringComparison.Ordinal);
        Assert.Contains("<!--#if (includeRest) -->", project, StringComparison.Ordinal);

        Assert.Contains("AddMPCoreBearerAuthentication", program, StringComparison.Ordinal);
        Assert.Contains("AddMPCoreCurrentActor", program, StringComparison.Ordinal);
        Assert.Contains("AddMPCoreAuthorization", program, StringComparison.Ordinal);
        Assert.Contains("AddMPCoreFailureHandling", program, StringComparison.Ordinal);
        Assert.Contains("AddMPCoreHttpFailureHandling", program, StringComparison.Ordinal);
        Assert.Contains("AddMPCoreProblemDetailsSecurityResponses", program, StringComparison.Ordinal);
        Assert.Contains("TransportEndpointGuard.Validate", program, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureEndpointDefaults", program, StringComparison.Ordinal);
        // Reflection is a description surface: mapped only inside its gate, and the gate is decided by
        // the environment with an explicit opt-in, never by the mere presence of the package.
        Assert.Single(Regex.Matches(program, "MapGrpcReflectionService"));
        Assert.Matches(new Regex(@"if \(enableGrpcReflection\)\s*\{[^}]*AddGrpcReflection\(\)", RegexOptions.Singleline), program);
        Assert.Matches(new Regex(@"if \(enableGrpcReflection\)\s*\{[^}]*MapGrpcReflectionService\(\)", RegexOptions.Singleline), program);
        Assert.Contains("DeveloperEndpoints.IsDescriptionSurfaceEnabled(", program, StringComparison.Ordinal);

        var gate = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Hosting/DeveloperEndpoints.cs"));
        Assert.Contains("configuration.GetValue<bool?>(key)", gate, StringComparison.Ordinal);
        Assert.Contains("environment.IsDevelopment()", gate, StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoints_are_bound_to_the_listener_port_and_never_to_the_host_header()
    {
        var program = File.ReadAllText(
            Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));
        var separation = File.ReadAllText(
            Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Hosting/TransportPortSeparation.cs"));

        // RequireHost evaluates the client-controlled Host header, so a gateway forwarding a host
        // name without a port would 404 every REST endpoint, health probes included.
        Assert.DoesNotContain("RequireHost", program, StringComparison.Ordinal);
        Assert.Contains("RequireListenerPort", program, StringComparison.Ordinal);
        Assert.Contains("app.UseTransportPortSeparation();", program, StringComparison.Ordinal);
        Assert.Contains("Connection.LocalPort", separation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_boot_guard_validates_the_declared_ports_against_the_kestrel_endpoints()
    {
        var guard = File.ReadAllText(
            Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Hosting/TransportEndpointGuard.cs"));

        Assert.Contains("Transport:RestPort", guard, StringComparison.Ordinal);
        Assert.Contains("Transport:GrpcPort", guard, StringComparison.Ordinal);
        Assert.Contains("Transport:EnforcePortSeparation", guard, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_pipeline_keeps_the_documented_middleware_order()
    {
        var program = File.ReadAllText(
            Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));

        var order = new[]
        {
            "app.UseMPCoreProblemDetails();",
            "app.UseMPCoreRequestContext();",
            "app.UseForwardedIdentityHeaderGuard();",
            "app.UseRouting();",
            "app.UseAuthentication();",
            "app.UseAuthorization();"
        };

        var positions = order.Select(step => program.IndexOf(step, StringComparison.Ordinal)).ToArray();

        Assert.All(positions, position => Assert.True(position > 0));
        Assert.Equal(positions.Order().ToArray(), positions);
    }

    [Fact]
    public void The_host_clears_the_default_logging_providers_before_the_foundation_registers_its_pipeline()
    {
        var program = File.ReadAllText(
            Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));
        var settings = File.ReadAllText(
            Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/appsettings.json"));

        // WebApplication.CreateBuilder adds the console, debug and event-source providers. Each prints a log
        // argument as it is, so a protobuf request of a sensitive service reaches the console whole. They are
        // cleared before the foundation registers MP Core's pipeline, because ClearProviders also removes a
        // provider registered earlier; the pipeline's console sink then keeps the logs on the console.
        var clear = program.IndexOf("builder.Logging.ClearProviders();", StringComparison.Ordinal);
        var foundation = program.IndexOf("builder.Services.AddMPCoreFoundation(", StringComparison.Ordinal);
        Assert.True(clear > 0, "Program.cs must clear the default logging providers.");
        Assert.True(clear < foundation, "ClearProviders must come before AddMPCoreFoundation, or it removes MP Core's provider too.");
        Assert.Contains("EnableConsoleLogExporter = builder.Configuration.GetValue(\"Observability:EnableConsoleLogExporter\", true)", program, StringComparison.Ordinal);
        Assert.Contains("\"EnableConsoleLogExporter\": true", settings, StringComparison.Ordinal);

        // No code of the template brings a provider back: they would print what the pipeline masks.
        var provider = new Regex(@"\.Add(Console|SimpleConsole|JsonConsole|SystemdConsole|Debug|EventSourceLogger|EventLog|TraceSource)\s*\(");
        var returned = Directory.EnumerateFiles(Path.Combine(TemplateRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => provider.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(TemplateRoot, file))
            .ToArray();
        Assert.Empty(returned);
    }

    [Fact]
    public void Kestrel_endpoints_are_declared_per_transport_with_authoritative_protocols()
    {
        var settings = File.ReadAllText(
            Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/appsettings.json"));

        Assert.Contains(
            "\"Grpc\": { \"Url\": \"http://0.0.0.0:8081\", \"Protocols\": \"Http2\" }",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Rest\": { \"Url\": \"http://0.0.0.0:8080\", \"Protocols\": \"Http1AndHttp2\" }",
            settings,
            StringComparison.Ordinal);
        Assert.Contains("\"EnforcePortSeparation\": true", settings, StringComparison.Ordinal);
        Assert.Contains("//#if (transport == \"both\")", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void The_template_carries_no_credential_realm_or_resolvable_identity_url()
    {
        var settings = File.ReadAllText(
            Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/appsettings.json"));

        Assert.Contains("https://identity.invalid/", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", settings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clientsecret", settings, StringComparison.OrdinalIgnoreCase);

        // The same replace-me discipline applied to Security:Authority and Security:Audiences also
        // covers the database and broker credentials, so no generated file carries a working one.
        Assert.DoesNotContain("Password=postgres", settings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Username=postgres", settings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guest:guest", settings, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Password=replace-me", settings, StringComparison.Ordinal);
        Assert.Contains("amqp://replace-me:replace-me@", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cli_requires_an_explicit_transport_and_writes_manifest_schema_version_four()
    {
        var cli = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Program.cs"));

        Assert.Contains("\"Missing required option --transport.\"", cli, StringComparison.Ordinal);
        Assert.Contains("\"--transport must be grpc, rest, or both.\"", cli, StringComparison.Ordinal);
        Assert.Contains("ManifestSchemaVersion = 4", cli, StringComparison.Ordinal);
        Assert.Contains($"CohortVersionValue = \"{CohortVersion}\"", cli, StringComparison.Ordinal);
    }

    [Fact]
    public void Continuous_integration_builds_and_tests_and_never_publishes_or_deploys()
    {
        // ADR-004, addenda of 2026-09-27: integration is automated, publication is a maintainer's act.
        var workflows = Directory.GetFiles(Path.Combine(RepositoryRoot, ".github/workflows"), "*.yml");
        Assert.NotEmpty(workflows);
        foreach (var workflow in workflows)
        {
            var text = File.ReadAllText(workflow);

            // No workflow holds a credential, and none may write to the repository.
            Assert.DoesNotContain("secrets.", text, StringComparison.Ordinal);
            Assert.Contains("permissions:\n  contents: read", text, StringComparison.Ordinal);
            Assert.DoesNotContain("contents: write", text, StringComparison.Ordinal);

            if (Path.GetFileName(workflow) == "release.yml")
            {
                continue;
            }

            Assert.DoesNotContain("nuget push", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NUGET_API_KEY", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("id-token", text, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(Path.Combine(RepositoryRoot, ".gitlab-ci.yml")));
        Assert.False(Directory.Exists(Path.Combine(RepositoryRoot, "deploy")));
        Assert.False(Directory.Exists(Path.Combine(RepositoryRoot, "charts")));
    }

    [Fact]
    public void Publication_is_started_and_approved_by_a_person_and_holds_no_key()
    {
        // ADR-004, addendum of 2026-09-27 on publication. One workflow may publish, and these are the
        // conditions under which it may. Each assertion is a way the workflow could be made to publish
        // without a person, or with a key that can leak.
        var text = File.ReadAllText(Path.Combine(RepositoryRoot, ".github/workflows/release.yml"));

        // Started by hand and by nothing else: a push, a tag or a timer must never publish.
        var triggers = text[text.IndexOf("\non:\n", StringComparison.Ordinal)..text.IndexOf("\npermissions:", StringComparison.Ordinal)];
        Assert.Contains("workflow_dispatch:", triggers, StringComparison.Ordinal);
        foreach (var trigger in new[] { "push:", "pull_request", "schedule:", "release:", "workflow_run:", "workflow_call:" })
        {
            Assert.DoesNotContain(trigger, triggers, StringComparison.Ordinal);
        }

        // Without the choice to publish, a run is a rehearsal.
        Assert.Contains("      publish:\n", triggers, StringComparison.Ordinal);
        Assert.Contains("        default: false", triggers, StringComparison.Ordinal);

        var publish = text[text.IndexOf("\n  publish:\n", StringComparison.Ordinal)..];
        var before = text[..text.IndexOf("\n  publish:\n", StringComparison.Ordinal)];

        // The job that publishes waits for a maintainer, and runs only after the files were verified.
        Assert.Contains("    if: inputs.publish\n", publish, StringComparison.Ordinal);
        Assert.Contains("    needs: pack-and-verify\n", publish, StringComparison.Ordinal);
        Assert.Contains("    environment: nuget\n", publish, StringComparison.Ordinal);
        Assert.Contains("shasum -a 256 -c SHA256SUMS.txt", publish, StringComparison.Ordinal);
        Assert.Contains("./eng/verify-release-artifacts.sh \"$VERSION\"", before, StringComparison.Ordinal);
        Assert.Contains("dotnet test MPCore.sln", before, StringComparison.Ordinal);

        // The key is issued to the run and lives an hour. Only the job that publishes can ask for it,
        // and the job that builds and runs the tests cannot.
        Assert.Contains("uses: NuGet/login@", publish, StringComparison.Ordinal);
        Assert.Contains("id-token: write", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("id-token", before, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget push", before, StringComparison.OrdinalIgnoreCase);

        // It publishes what it built, and nothing it fetched from somewhere else.
        Assert.DoesNotContain("checkout", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_host_has_a_check_behind_every_probe()
    {
        // Found by the Storefront sample, with a probe and a stopped database. A host that registers no
        // health check answers UNKNOWN on grpc.health.v1.Health, which a gRPC probe reads as not
        // serving, and answers Healthy on /health/ready without having asked anything.
        var program = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));
        var checks = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Hosting/HostHealthChecks.cs"));

        // Registered once, outside every transport section, so a gRPC-only host has the checks too.
        var registration = program.IndexOf("builder.Services.AddHostHealthChecks();", StringComparison.Ordinal);
        Assert.True(registration > 0);
        Assert.True(registration < program.IndexOf("// #if (includeGrpc)\nbuilder.Services.AddGrpc()", StringComparison.Ordinal));
        Assert.DoesNotContain("builder.Services.AddHealthChecks();", program, StringComparison.Ordinal);

        Assert.Contains(".AddCheck(\"process\", static () => HealthCheckResult.Healthy(), tags: [Live])", checks, StringComparison.Ordinal);
        Assert.Contains(".AddCheck<DatabaseReadinessCheck>(\"database\", tags: [Ready])", checks, StringComparison.Ordinal);
        Assert.Contains("CanConnectAsync(cancellationToken)", checks, StringComparison.Ordinal);

        // Alive asks the process only: a database that is down must not restart a healthy process.
        Assert.DoesNotContain("Predicate = static _ => false", program, StringComparison.Ordinal);
        Assert.Contains("Predicate = static check => check.Tags.Contains(HostHealthChecks.Live)", program, StringComparison.Ordinal);
        Assert.Contains(
            "options.Services.Map(HostHealthChecks.Live, static check => check.Tags.Contains(HostHealthChecks.Live))",
            program,
            StringComparison.Ordinal);

        var gettingStarted = File.ReadAllText(Path.Combine(TemplateRoot, "docs/getting-started.md"));
        Assert.Contains("Hosting/HostHealthChecks.cs", gettingStarted, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_host_never_configures_anonymous_health_access_through_mpcore()
    {
        var program = File.ReadAllText(
            Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));

        // Regression: a packaged 0.2.0-alpha.2 template generated an options callback assigning
        // MPCoreAuthorizationOptions.AllowAnonymousHealthEndpoints, a property the runtime package
        // deliberately does not expose, so every generated host failed with CS1061.
        Assert.Contains("builder.Services.AddMPCoreAuthorization();", program, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "options.AllowAnonymousHealthEndpoints", program, StringComparison.Ordinal);

        // Matches any argument, so renaming the lambda parameter does not defeat the assertion.
        Assert.DoesNotMatch(
            new Regex(@"AddMPCoreAuthorization\(\s*[^)\s]", RegexOptions.CultureInvariant),
            program);

        // Anonymous health access stays a host decision, applied where the host maps the probes.
        Assert.Contains(
            "builder.Configuration.GetValue(\"Security:AllowAnonymousHealthEndpoints\", true)",
            program,
            StringComparison.Ordinal);

        // Exactly the four health probes may be anonymous. The business probes must not be: an
        // assertion that only counted AllowAnonymous() calls would pass if someone moved the call
        // from grpcHealthEndpoint to grpcProbeEndpoint and silently opened PlatformProbe.
        var anonymous = Regex.Matches(program, @"(?<endpoint>\w+)\.AllowAnonymous\(\);")
            .Select(match => match.Groups["endpoint"].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "grpcHealthEndpoint", "grpcReflection", "livenessEndpoint", "openApiDocument",
                "readinessEndpoint", "startupEndpoint"
            },
            anonymous);

        // The two description surfaces are anonymous only in Development. Each AllowAnonymous on them
        // must sit inside the same guard, and that guard must be the environment, not a flag a typo
        // could set in production.
        Assert.Contains("var anonymousDescriptionSurface = builder.Environment.IsDevelopment();", program, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(program, @"if \(anonymousDescriptionSurface\)").Count);
        Assert.DoesNotContain("AllowAnonymous(); // reflection", program, StringComparison.Ordinal);

        // The UI shell cannot carry a token, so it is served ahead of authentication and only in
        // Development; outside Development it must not be mounted at all.
        var ui = program.IndexOf("app.UseSwaggerUI(", StringComparison.Ordinal);
        var authentication = program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        Assert.True(ui > 0 && ui < authentication, "Swagger UI must be mounted before authentication");
        Assert.Contains("if (enableOpenApi && anonymousDescriptionSurface)", program, StringComparison.Ordinal);
        Assert.DoesNotContain("grpcProbeEndpoint.AllowAnonymous", program, StringComparison.Ordinal);
        Assert.DoesNotContain("restProbeEndpoints.AllowAnonymous", program, StringComparison.Ordinal);

        // The named-receiver regex above cannot see a chained form such as
        // app.MapGet("/x", h).AllowAnonymous(), so the raw call count is pinned as well: the set is
        // then protected against addition, not only against renaming.
        Assert.Equal(anonymous.Length, Regex.Matches(program, @"\.AllowAnonymous\(\)").Count);
    }

    [Fact]
    public void The_cohort_version_is_identical_across_the_cli_the_template_and_the_runtime()
    {
        var cli = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Program.cs"));
        var cliProject = File.ReadAllText(
            Path.Combine(RepositoryRoot, "tools/MPCore.Cli/MPCore.Cli.csproj"));
        var templateProject = File.ReadAllText(
            Path.Combine(RepositoryRoot, "tools/MPCore.Templates/MPCore.Templates.csproj"));
        var runtimeProperties = File.ReadAllText(
            Path.Combine(RepositoryRoot, "src/Directory.Build.props"));

        // Packed as MPCore.Cli's PackageReadmeFile, so it is the front page a consumer reads on the
        // feed. It declares the default version and the manifest schema and therefore drifts like
        // any other declaration.
        var cliReadme = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/README.md"));

        using var definition = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(TemplateRoot, ".template.config/template.json")));
        var symbols = definition.RootElement.GetProperty("symbols");

        // A stable version has no suffix, and then none may be declared: a suffix left behind in one of
        // the three project files would ship that one as a prerelease of the same number.
        var prefix = CohortVersion.Split('-')[0];
        var suffix = CohortVersion.Length > prefix.Length ? CohortVersion[(prefix.Length + 1)..] : null;
        var expectedPrefix = $"<VersionPrefix>{prefix}</VersionPrefix>";
        var expectedSuffix = suffix is null ? string.Empty : $"<VersionSuffix>{suffix}</VersionSuffix>";
        if (suffix is null)
        {
            Assert.All(
                new[] { cliProject, templateProject, runtimeProperties },
                static declaration => Assert.DoesNotContain("<VersionSuffix>", declaration, StringComparison.Ordinal));
        }

        // A CLI paired with a differently versioned template package generates source against an
        // API surface that does not exist. Drift between these five declarations is the mechanism
        // that shipped a stale template under a current version number.
        Assert.Contains($"CohortVersionValue = \"{CohortVersion}\"", cli, StringComparison.Ordinal);
        Assert.Contains(expectedPrefix, cliProject, StringComparison.Ordinal);
        Assert.Contains(expectedSuffix, cliProject, StringComparison.Ordinal);
        Assert.Contains(expectedPrefix, templateProject, StringComparison.Ordinal);
        Assert.Contains(expectedSuffix, templateProject, StringComparison.Ordinal);
        Assert.Contains(expectedPrefix, runtimeProperties, StringComparison.Ordinal);
        Assert.Contains(expectedSuffix, runtimeProperties, StringComparison.Ordinal);
        Assert.Equal(
            CohortVersion,
            symbols.GetProperty("templateVersion").GetProperty("defaultValue").GetString());
        Assert.Equal(
            CohortVersion,
            symbols.GetProperty("mpcoreVersion").GetProperty("defaultValue").GetString());

        Assert.Contains(CohortVersion, cliReadme, StringComparison.Ordinal);
        Assert.DoesNotContain("0.2.0-alpha.2", cliReadme, StringComparison.Ordinal);
        Assert.DoesNotContain("0.2.0-alpha.4", cliReadme, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 4", cliReadme, StringComparison.Ordinal);
        Assert.DoesNotContain("\"schemaVersion\": 3", cliReadme, StringComparison.Ordinal);
    }

    [Fact]
    public void The_template_ships_a_version_marker_the_cli_can_verify_after_generation()
    {
        var marker = Path.Combine(TemplateRoot, ".mpcore-template-version");
        var cli = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Program.cs"));
        var templateProject = File.ReadAllText(
            Path.Combine(RepositoryRoot, "tools/MPCore.Templates/MPCore.Templates.csproj"));

        Assert.True(File.Exists(marker));
        Assert.Equal("MPCORE_TEMPLATE_VERSION", File.ReadAllText(marker).Trim());

        // NoDefaultExcludes keeps the dotted marker inside the packed content; without it the CLI
        // would reject every generation because the marker never reaches the consumer.
        Assert.Contains("<NoDefaultExcludes>true</NoDefaultExcludes>", templateProject, StringComparison.Ordinal);
        Assert.Contains("Content Include=\"content/**/*\"", templateProject, StringComparison.Ordinal);

        Assert.Contains("TemplateVersionMarkerFileName", cli, StringComparison.Ordinal);
        Assert.Contains("Template/CLI version mismatch", cli, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_skill_has_a_codex_and_a_claude_adapter_and_exactly_one_body()
    {
        var canonical = Directory.GetDirectories(Path.Combine(TemplateRoot, ".mpcore/skills"))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(canonical);

        foreach (var target in new[] { ".agents/skills", ".claude/skills" })
        {
            var adapters = Directory.GetDirectories(Path.Combine(TemplateRoot, target))
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            // Codex reads .agents/skills and Claude Code reads .claude/skills, so each runtime needs
            // its own layout. Both must cover exactly the same skills or one assistant silently sees
            // fewer than the other.
            Assert.Equal(canonical, adapters);

            foreach (var skill in canonical)
            {
                var adapter = File.ReadAllText(Path.Combine(TemplateRoot, target, skill!, "SKILL.md"));
                var body = File.ReadAllText(Path.Combine(TemplateRoot, ".mpcore/skills", skill!, "SKILL.md"));

                // Codex requires name and description; Claude Code uses description to decide when a
                // skill applies. Both must match the canonical body or discovery diverges from content.
                Assert.Equal(FrontMatter(body, "name"), FrontMatter(adapter, "name"));
                Assert.Equal(FrontMatter(body, "description"), FrontMatter(adapter, "description"));

                // The adapter must stay a pointer, and the pointer must actually resolve from the
                // adapter's own directory. Asserting the string alone passed while every link was one
                // level short, which is how a broken pointer shipped past a green test.
                var link = Regex.Match(adapter, @"\((\.\./[^)]*SKILL\.md)\)").Groups[1].Value;
                Assert.False(string.IsNullOrEmpty(link), $"{target}/{skill} has no canonical link");
                var resolved = Path.GetFullPath(Path.Combine(TemplateRoot, target, skill!, link));
                Assert.True(File.Exists(resolved), $"{target}/{skill} link does not resolve: {link}");
                Assert.Equal(
                    Path.GetFullPath(Path.Combine(TemplateRoot, ".mpcore/skills", skill!, "SKILL.md")),
                    resolved);
                Assert.True(adapter.Length < body.Length / 2, $"{target}/{skill} looks like a duplicated body");
            }
        }
    }

    [Fact]
    public void The_ai_entry_points_are_short_and_reference_files_that_exist()
    {
        foreach (var (entry, skillDirectory) in new[] { ("AGENTS.md", ".agents/skills"), ("CLAUDE.md", ".claude/skills") })
        {
            var path = Path.Combine(TemplateRoot, entry);
            Assert.True(File.Exists(path), $"{entry} is missing");
            var text = File.ReadAllText(path);

            // Entry points are read on every task. Detailed guidance is loaded on demand from the
            // skills instead of injected into every session.
            Assert.True(text.Split('\n').Length < 45, $"{entry} is too long to be an entry point");
            Assert.Contains(".mpcore/template-manifest.json", text, StringComparison.Ordinal);
            Assert.Contains(".mpcore/skills/INVENTORY.md", text, StringComparison.Ordinal);
            Assert.Contains(skillDirectory, text, StringComparison.Ordinal);
            Assert.Contains("ICurrentActorAccessor", text, StringComparison.Ordinal);

            // Listing the path is not enough: the discovered file is an adapter with no instructions,
            // so the entry point must send the assistant to the canonical body.
            Assert.Contains(".mpcore/skills/<name>/SKILL.md", text, StringComparison.Ordinal);
            Assert.Contains("only an adapter", text, StringComparison.Ordinal);
        }

        Assert.True(File.Exists(Path.Combine(TemplateRoot, ".mpcore/skills/INVENTORY.md")));

        // The human-facing guides are the discoverable entry point: a developer should not have to
        // find guidance inside dot-directories.
        var readme = File.ReadAllText(Path.Combine(TemplateRoot, "README.md"));
        foreach (var link in new[] { "docs/getting-started.md", "docs/development-workflow.md", "docs/examples/", "docs/ai-skills.md" })
        {
            Assert.Contains(link, readme, StringComparison.Ordinal);
        }

        foreach (var guide in new[] { "docs/getting-started.md", "docs/development-workflow.md", "docs/ai-skills.md", "docs/examples/README.md" })
        {
            Assert.True(File.Exists(Path.Combine(TemplateRoot, guide)), $"{guide} is missing");
        }
        Assert.True(File.Exists(Path.Combine(TemplateRoot, "docs/development-workflow.md")));
        Assert.True(File.Exists(Path.Combine(TemplateRoot, ".mpcore/skills/BUNDLE.json")));
    }

    [Fact]
    public void No_shipped_ai_file_carries_a_machine_specific_path_or_product_business_behaviour()
    {
        var files = Directory.GetFiles(Path.Combine(TemplateRoot, ".mpcore"), "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(TemplateRoot, ".agents"), "*", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(Path.Combine(TemplateRoot, ".claude"), "*", SearchOption.AllDirectories))
            .Concat(new[] { Path.Combine(TemplateRoot, "AGENTS.md"), Path.Combine(TemplateRoot, "CLAUDE.md") });

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            // Guidance that points at one developer's disk, or at the governance repository, does not
            // travel with the package.
            Assert.DoesNotContain("/Users/", text, StringComparison.Ordinal);
            Assert.DoesNotContain("C:\\", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".codex/plugins", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Payment/Governance", text, StringComparison.Ordinal);
            Assert.DoesNotContain("READY_FOR_DEV", text, StringComparison.Ordinal);

            // No product may be named, and no example may become generated business logic.
            foreach (var product in new[] { "Ganjineh", "SanaCash", "IDR" })
            {
                Assert.DoesNotContain(product, text, StringComparison.Ordinal);
            }

            Assert.False(name.EndsWith(".cs", StringComparison.Ordinal), $"{name} ships code as guidance");
        }
    }

    [Fact]
    public void Consumer_skills_exclude_framework_maintenance_authority()
    {
        var skills = Directory.GetDirectories(Path.Combine(TemplateRoot, ".mpcore/skills"))
            .Select(Path.GetFileName)
            .ToArray();

        // A generated product repository consumes MP Core; it does not evolve or publish it. Shipping
        // those procedures would imply an authority the repository does not have.
        Assert.DoesNotContain("mpcore-evolve-package", skills);
        Assert.DoesNotContain("mpcore-release", skills);
        Assert.DoesNotContain("mpcore-scaffold-backend", skills);

        var inventory = File.ReadAllText(Path.Combine(TemplateRoot, ".mpcore/skills/INVENTORY.md"));
        Assert.Contains("framework repository", inventory, StringComparison.Ordinal);
    }

    private static string FrontMatter(string document, string field)
    {
        var line = document.Split('\n').First(candidate => candidate.StartsWith($"{field}: ", StringComparison.Ordinal));
        return line[(field.Length + 2)..].Trim();
    }

    [Fact]
    public void The_ai_tooling_option_is_wired_end_to_end()
    {
        var cli = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Program.cs"));
        using var definition = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(TemplateRoot, ".template.config/template.json")));
        var symbols = definition.RootElement.GetProperty("symbols");
        var aiTooling = symbols.GetProperty("aiTooling");

        // The CLI must validate every value and forward the choice, or the manifest records a
        // selection that did not drive generation.
        Assert.Contains("\"--ai-tooling must be both, codex, claude, or none.\"", cli, StringComparison.Ordinal);
        Assert.Contains("\"--ai-tooling was given without a value.\"", cli, StringComparison.Ordinal);
        Assert.Contains("\"--aiTooling\", aiTooling", cli, StringComparison.Ordinal);
        Assert.Contains("aiTooling,", cli, StringComparison.Ordinal);

        Assert.Equal("both", aiTooling.GetProperty("defaultValue").GetString());
        Assert.Equal(
            new[] { "both", "codex", "claude", "none" },
            aiTooling.GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("choice").GetString()));

        // The recorded value comes from the choice itself, so BUNDLE.json cannot disagree with the
        // value that produced the repository.
        Assert.Equal("MPCORE_AI_TOOLING", aiTooling.GetProperty("replaces").GetString());

        Assert.Equal("(aiTooling == \"codex\" || aiTooling == \"both\")",
            symbols.GetProperty("includeCodex").GetProperty("value").GetString());
        Assert.Equal("(aiTooling == \"claude\" || aiTooling == \"both\")",
            symbols.GetProperty("includeClaude").GetProperty("value").GetString());
        Assert.Equal("(aiTooling != \"none\")",
            symbols.GetProperty("includeAiSkills").GetProperty("value").GetString());
    }

    [Fact]
    public void The_bundle_manifest_declares_only_the_adapters_that_are_generated()
    {
        var bundle = File.ReadAllText(Path.Combine(TemplateRoot, ".mpcore/skills/BUNDLE.json"));

        // Declaring a directory that generation excluded makes the machine-readable manifest lie.
        Assert.Contains("//#if (includeCodex)", bundle, StringComparison.Ordinal);
        Assert.Contains("//#if (includeClaude)", bundle, StringComparison.Ordinal);
        Assert.Contains("//#if (includeCodex && includeClaude)", bundle, StringComparison.Ordinal);
        Assert.Contains("\"aiTooling\": \"MPCORE_AI_TOOLING\"", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Consumer_adapters_do_not_reference_framework_maintenance_tooling()
    {
        foreach (var target in new[] { ".agents/skills", ".claude/skills" })
        {
            foreach (var adapter in Directory.GetFiles(Path.Combine(TemplateRoot, target), "SKILL.md", SearchOption.AllDirectories))
            {
                // eng/ does not exist in a generated repository, so telling the assistant to run a
                // script from it is an instruction that cannot be followed.
                Assert.DoesNotContain("eng/sync-ai-skill-adapters.py", File.ReadAllText(adapter), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Every_relative_link_in_the_visible_guides_resolves()
    {
        var docs = Directory.GetFiles(Path.Combine(TemplateRoot, "docs"), "*.md", SearchOption.AllDirectories)
            .Append(Path.Combine(TemplateRoot, "README.md"));

        foreach (var file in docs)
        {
            var directory = Path.GetDirectoryName(file)!;
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\]\((?!https?:)([^)#]+)(?:#[^)]*)?\)"))
            {
                var target = match.Groups[1].Value;
                var resolved = Path.GetFullPath(Path.Combine(directory, target));

                // A broken link in shipped guidance fails silently: the reader simply does not find
                // the page and concludes the guidance does not exist.
                Assert.True(File.Exists(resolved) || Directory.Exists(resolved),
                    $"{Path.GetFileName(file)} links to '{target}' which does not exist");
            }
        }
    }

    [Fact]
    public void The_skill_guide_is_generated_from_the_canonical_inventory()
    {
        var guide = File.ReadAllText(Path.Combine(TemplateRoot, "docs/ai-skills.md"));
        var canonical = Directory.GetDirectories(Path.Combine(TemplateRoot, ".mpcore/skills"))
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        foreach (var skill in canonical)
        {
            // Documented, and pointing at the one body rather than restating it.
            Assert.Contains($"### {skill}", guide, StringComparison.Ordinal);
            Assert.Contains($"../.mpcore/skills/{skill}/SKILL.md", guide, StringComparison.Ordinal);
        }

        // A guide shorter than the bodies it indexes is a guide, not a second copy of the skills.
        var bodyLength = Directory.GetFiles(Path.Combine(TemplateRoot, ".mpcore/skills"), "SKILL.md", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        Assert.True(guide.Length < bodyLength / 2, "ai-skills.md looks like a duplicated skill corpus");
    }

    [Fact]
    public void The_cli_reports_the_generated_guides_and_never_promises_absent_ones()
    {
        var cli = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Program.cs"));

        Assert.Contains("\"README.md\"", cli, StringComparison.Ordinal);
        Assert.Contains("\"getting-started.md\"", cli, StringComparison.Ordinal);
        Assert.Contains("\"development-workflow.md\"", cli, StringComparison.Ordinal);

        // The skills guide is only generated when ai tooling is, so the message must be conditional.
        var index = cli.IndexOf("ai-skills.md", StringComparison.Ordinal);
        Assert.True(index > 0, "the CLI never mentions the skills guide");
        var preceding = cli[..index];
        Assert.Contains("aiTooling != \"none\"", preceding, StringComparison.Ordinal);
    }

    [Fact]
    public void The_configure_command_plans_before_it_changes_and_owns_a_bounded_set_of_files()
    {
        var configure = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Configure.cs"));

        // Planning is the default. A tool that mutates a live repository by default is one mistyped
        // flag away from destroying uncommitted work.
        Assert.Contains("var apply = options.ContainsKey(\"apply\");", configure, StringComparison.Ordinal);
        Assert.Contains("if (!apply)", configure, StringComparison.Ordinal);

        // Only generator-owned skills may be removed; a developer's own skill has no mpcore- prefix.
        Assert.Contains("StartsWith(\"mpcore-\", StringComparison.Ordinal)", configure, StringComparison.Ordinal);

        // Recovery restores from a backup taken in the same run, never from version control: the
        // developer's uncommitted work is not the tool's to discard.
        Assert.Contains("Restore(project, backup)", configure, StringComparison.Ordinal);
        Assert.DoesNotContain("git reset", configure, StringComparison.Ordinal);
        Assert.DoesNotContain("git checkout", configure, StringComparison.Ordinal);
        Assert.DoesNotContain("git clean", configure, StringComparison.Ordinal);

        // The manifest is written last, so it never describes a state the repository is not in.
        var manifestSave = configure.IndexOf("manifest.Save(manifestFile)", StringComparison.Ordinal);
        var applyLoop = configure.IndexOf("Apply(project, change, options, appSettings)", StringComparison.Ordinal);
        Assert.True(applyLoop > 0 && manifestSave > applyLoop, "the manifest must be saved after the file changes");

        // Migrations that cannot be performed safely are refused with reasoning, not half-applied.
        foreach (var refused in new[] { "shape", "organization", "component" })
        {
            Assert.Contains($"(\"{refused}\", manifest.Get(\"{refused}\")", configure, StringComparison.Ordinal);
        }

        Assert.Contains("TransportGuidance", configure, StringComparison.Ordinal);
        Assert.Contains("MessagingGuidance", configure, StringComparison.Ordinal);
    }

    [Fact]
    public void Secrets_are_refused_at_generation_and_at_configuration()
    {
        foreach (var file in new[] { "tools/MPCore.Cli/Program.cs", "tools/MPCore.Cli/Configure.cs" })
        {
            var source = File.ReadAllText(Path.Combine(RepositoryRoot, file));

            // A command line reaches shell history and process listings, so a credential must never be
            // accepted there even when the caller offers one.
            Assert.Contains("LooksLikeSecret", source, StringComparison.Ordinal);
        }

        var cli = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Program.cs"));

        // Connection values that are not secret may be supplied; a password or connection string may not.
        Assert.Contains("--security-authority", cli, StringComparison.Ordinal);
        Assert.Contains("--security-audience", cli, StringComparison.Ordinal);
        Assert.DoesNotContain("--connection-string", cli, StringComparison.Ordinal);
        Assert.DoesNotContain("--client-secret", cli, StringComparison.Ordinal);

        // The environment must be checked before generating, not discovered through a compile error.
        Assert.Contains("EffectiveSdkAsync", cli, StringComparison.Ordinal);
    }

    [Fact]
    public void The_settings_documentation_only_promises_commands_that_exist()
    {
        var guide = File.ReadAllText(Path.Combine(TemplateRoot, "docs/getting-started.md"));
        var configure = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Configure.cs"));

        Assert.Contains("mpcore configure --project", guide, StringComparison.Ordinal);

        // Every option the guide tells a developer to run must be implemented, or the documentation is
        // an instruction that fails when followed.
        foreach (var option in new[] { "--ai-tooling", "--cache", "--business-audit", "--timeseries", "--mpcore-version", "--security-authority", "--security-audience" })
        {
            Assert.Contains(option, guide, StringComparison.Ordinal);
            Assert.Contains(option.TrimStart('-'), configure, StringComparison.Ordinal);
        }

        // And it must not invent one.
        foreach (var invented in new[] { "mpcore migrate", "mpcore upgrade", "mpcore rename", "mpcore add" })
        {
            Assert.DoesNotContain(invented, guide, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Ownership_is_decided_by_content_not_by_file_name()
    {
        var configure = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Configure.cs"));

        // A name proves nothing: AGENTS.md, CLAUDE.md and an mpcore-* skill body are all generator
        // output that a developer may since have edited. Ownership is decided by comparing against the
        // generator's own output for the settings the manifest records.
        Assert.Contains("IsUserModified", configure, StringComparison.Ordinal);

        // And the reference must be the template this repository records, not whatever happens to be
        // installed: a newer one flags every file as edited, a stale one flags edited files as pristine.
        Assert.Contains("var required = manifest.Get(\"templateVersion\")", configure, StringComparison.Ordinal);
        Assert.Contains("installed != required", configure, StringComparison.Ordinal);

        // Conflict guidance must not steer the developer toward discarding their work.
        Assert.Contains("nothing needs to be discarded", configure, StringComparison.Ordinal);
        Assert.DoesNotContain("restore the file to its generated", configure, StringComparison.Ordinal);
        Assert.Contains("baseline = await MaterializeTemplateAsync", configure, StringComparison.Ordinal);
        Assert.Contains("FilesMatch", configure, StringComparison.Ordinal);

        // And the conflict must stop the run before anything is written.
        var conflictCheck = configure.IndexOf("conflicts.Count > 0", StringComparison.Ordinal);
        var firstWrite = configure.IndexOf("Apply(project, change, options, appSettings)", StringComparison.Ordinal);
        Assert.True(conflictCheck > 0 && conflictCheck < firstWrite,
            "the modification check must run before any file is written");
    }

    [Fact]
    public void A_failed_apply_undoes_everything_that_run_did()
    {
        var configure = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Configure.cs"));

        // Restoring changed files is not enough: files the run created must go too, or the repository
        // is left in a state that is neither the old one nor the new one.
        Assert.Contains("applied.Where(c => c.Kind == Kind.Add)", configure, StringComparison.Ordinal);
        Assert.Contains("Removed {removed} file(s) this run added", configure, StringComparison.Ordinal);
        Assert.Contains("Restore(project, backup)", configure, StringComparison.Ordinal);

        // The manifest is only written on success, so a failure leaves it describing reality.
        var manifestSave = configure.IndexOf("manifest.Save(manifestFile)", StringComparison.Ordinal);
        var catchBlock = configure.IndexOf("catch (Exception exception) when", StringComparison.Ordinal);
        Assert.True(manifestSave > 0 && manifestSave < catchBlock);
    }

    [Fact]
    public void Generated_files_that_record_the_version_move_with_it()
    {
        var configure = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Configure.cs"));

        // Changing the pin without refreshing what documents it leaves the repository describing a
        // version it no longer uses.
        Assert.Contains("records the MP Core version", configure, StringComparison.Ordinal);

        // The guides carry ai-tooling-conditional sections, so an ai-tooling change must refresh them
        // too. Leaving them stale is not merely cosmetic: the stale file then looks like a user edit
        // and blocks every later run.
        Assert.Contains("documents the assistant tooling", configure, StringComparison.Ordinal);
        foreach (var file in new[]
                 {
                     "README.md", "docs/getting-started.md", "docs/architecture.md", "docs/capabilities.md",
                     ".mpcore/skills/BUNDLE.json",
                 })
        {
            Assert.Contains($"\"{file}\"", configure, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_sdk_check_resolves_the_effective_sdk_not_the_installed_list()
    {
        var cli = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Program.cs"));

        // A global.json above the output path can select a different SDK, so the presence of 10.x in
        // --list-sdks does not answer the question. `dotnet --version` resolves what this directory
        // will actually use, and fails when the pinned SDK is missing.
        Assert.Contains("EffectiveSdkAsync", cli, StringComparison.Ordinal);
        Assert.Contains("\"--version\"", cli, StringComparison.Ordinal);
        Assert.Contains("WorkingDirectory = probe", cli, StringComparison.Ordinal);
        Assert.Contains("FindGlobalJson", cli, StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentList.Add(\"--list-sdks\")", cli, StringComparison.Ordinal);
    }

    [Fact]
    public void The_architecture_documents_are_reachable_and_driven_by_the_manifest()
    {
        var readme = File.ReadAllText(Path.Combine(TemplateRoot, "README.md"));
        foreach (var link in new[] { "docs/architecture.md", "docs/capabilities.md" })
        {
            Assert.Contains(link, readme, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(TemplateRoot, link)), $"{link} is missing");
        }

        var architecture = File.ReadAllText(Path.Combine(TemplateRoot, "docs/architecture.md"));

        // The project-specific document must render the real selections, not one text for every case.
        foreach (var token in new[] { "MPCORE_SHAPE_LABEL", "MPCORE_TRANSPORT_LABEL", "MPCORE_MESSAGING_LABEL", "MPCORE_AI_TOOLING" })
        {
            Assert.Contains(token, architecture, StringComparison.Ordinal);
        }

        // Sections that only apply to some selections must be conditional, or a REST-only project
        // documents a gRPC package it does not reference.
        Assert.Contains("<!--#if (includeRest) -->", architecture, StringComparison.Ordinal);
        Assert.Contains("<!--#if (includeGrpc) -->", architecture, StringComparison.Ordinal);
        Assert.Contains("<!--#if (shape == \"modular-monolith\") -->", architecture, StringComparison.Ordinal);
        Assert.Contains("<!--#if (messaging != \"none\") -->", architecture, StringComparison.Ordinal);

        // A speculative topology must be labelled as such, not presented as deployed infrastructure.
        Assert.Contains("suggested topology, not infrastructure that exists", architecture, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_mermaid_block_is_well_formed_and_declares_a_diagram_type()
    {
        var known = new[] { "flowchart", "graph", "sequenceDiagram", "classDiagram", "stateDiagram", "erDiagram" };

        foreach (var file in Directory.GetFiles(Path.Combine(TemplateRoot, "docs"), "*.md", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            var open = 0;
            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].TrimStart().StartsWith("```mermaid", StringComparison.Ordinal))
                {
                    continue;
                }

                open++;

                // The first content line decides whether a renderer can parse it at all.
                var first = lines.Skip(index + 1).FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
                Assert.True(known.Any(k => first.StartsWith(k, StringComparison.Ordinal)),
                    $"{Path.GetFileName(file)}: mermaid block starts with '{first}', which is not a diagram type");

                // And it must be closed, or the rest of the document renders as code.
                var close = Array.FindIndex(lines, index + 1, l => l.Trim() == "```");
                Assert.True(close > index, $"{Path.GetFileName(file)}: unclosed mermaid block");
            }

            if (Path.GetFileName(file) == "architecture.md")
            {
                Assert.True(open >= 3, "the architecture document should carry component, layer and flow diagrams");
            }
        }
    }

    [Fact]
    public void The_capability_catalogue_does_not_claim_what_the_framework_lacks()
    {
        var catalogue = File.ReadAllText(Path.Combine(TemplateRoot, "docs/capabilities.md"));
        var runtime = Path.Combine(RepositoryRoot, "src");

        // Claiming a capability that does not exist is worse than omitting it: the reader plans around
        // it. Each of these is absent from the runtime packages, so the catalogue must say so.
        foreach (var (capability, marker) in new (string, string)[]
                 {
                     // Everything once listed here (Redis, outbox, Timescale, audit, the idempotency
                     // store) has since been implemented and moved to a positive test; these are what is
                     // still absent.
                     ("Continuous aggregates", "ContinuousAggregate"),
                     ("Generated audit endpoint", "MapMPCoreAudit"),
                     ("Automatic migration on startup", "MigrateOnStartup"),
                     ("Inbound rate limiting", "AddRateLimiter"),
                 })
        {
            var implemented = Directory
                .GetFiles(runtime, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/obj/", StringComparison.Ordinal))
                .Any(f => File.ReadAllText(f).Contains(marker, StringComparison.Ordinal));

            Assert.False(implemented, $"{marker} now exists in the runtime; the catalogue must be updated");

            var row = catalogue.Split('\n').FirstOrDefault(l => l.Contains(capability, StringComparison.Ordinal));
            Assert.NotNull(row);
            Assert.Contains("Not available", row!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Business_audit_is_a_choice_wired_through_template_cli_configure_and_docs()
    {
        using var template = JsonDocument.Parse(File.ReadAllText(Path.Combine(TemplateRoot, ".template.config/template.json")));
        var symbol = template.RootElement.GetProperty("symbols").GetProperty("businessAudit");
        Assert.Equal("none", symbol.GetProperty("defaultValue").GetString());
        Assert.Equal(new[] { "none", "postgresql" }, symbol.GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("choice").GetString()));
        Assert.Equal("(businessAudit == \"postgresql\")", template.RootElement.GetProperty("symbols").GetProperty("includeBusinessAudit").GetProperty("value").GetString());
        var modifier = template.RootElement.GetProperty("sources")[0].GetProperty("modifiers").EnumerateArray()
            .Single(m => m.GetProperty("condition").GetString() == "(!includeBusinessAudit)");
        Assert.Equal(
            new[] { "src/MPCore.Backend.Infrastructure/Audit/**" },
            modifier.GetProperty("exclude").EnumerateArray().Select(e => e.GetString()));

        // Every consumer of the choice gates on the same symbol: package, model, registration, policy file.
        var infrastructure = Path.Combine(TemplateRoot, "src/MPCore.Backend.Infrastructure");
        Assert.Contains("<!--#if (includeBusinessAudit) -->\n    <PackageReference Include=\"MPCore.Audit.EntityFrameworkCore.PostgreSql\"", File.ReadAllText(Path.Combine(infrastructure, "MPCore.Backend.Infrastructure.csproj")), StringComparison.Ordinal);
        Assert.Contains("//#if (includeBusinessAudit)\n        modelBuilder.ApplyMPCoreAudit();", File.ReadAllText(Path.Combine(infrastructure, "Persistence/AppDbContext.cs")), StringComparison.Ordinal);
        var registration = File.ReadAllText(Path.Combine(infrastructure, "DependencyInjection.cs"));
        Assert.Contains(".UseMPCoreAudit(provider)", registration, StringComparison.Ordinal);
        Assert.Contains("AddMPCoreWolverineDbContext<AppDbContext>", registration, StringComparison.Ordinal); // never plain AddDbContext: Wolverine refuses service location
        Assert.DoesNotContain("AddMPCorePostgreSql<AppDbContext>", registration, StringComparison.Ordinal);
        Assert.Contains("AddMPCoreAudit<AppDbContext>(AuditPolicyConfiguration.Configure)", registration, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(infrastructure, "Audit/AuditPolicyConfiguration.cs")));

        var cli = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Program.cs"));
        Assert.Contains("\"--business-audit must be none or postgresql.\"", cli, StringComparison.Ordinal);
        Assert.Contains("\"--businessAudit\", businessAudit,", cli, StringComparison.Ordinal);
        Assert.Contains("            businessAudit,\n            cache,\n            timeseries,\n            aiTooling,", cli, StringComparison.Ordinal);
        var configure = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Configure.cs"));
        Assert.Contains("\"--businessAudit\", businessAudit ?? manifest.GetOrDefault(\"businessAudit\", \"none\")", configure, StringComparison.Ordinal);
        Assert.Contains("--business-audit none|postgresql      add or remove the business audit trail wiring", configure, StringComparison.Ordinal);

        // The catalogue tells the truth in both directions: Supported when selected, and an honest
        // "Not selected" — never "Not available", which would deny the framework has it — otherwise.
        var catalogue = File.ReadAllText(Path.Combine(TemplateRoot, "docs/capabilities.md"));
        Assert.Contains("<!--#if (includeBusinessAudit) -->\n| Entity change and business-event audit | Supported |", catalogue, StringComparison.Ordinal);
        Assert.Contains("<!--#else -->\n| Entity change and business-event audit | Not selected |", catalogue, StringComparison.Ordinal);
        Assert.Contains("| Generated audit endpoint | **Not available** |", catalogue, StringComparison.Ordinal);
        Assert.Contains("## Business audit path", File.ReadAllText(Path.Combine(TemplateRoot, "docs/architecture.md")), StringComparison.Ordinal);
        Assert.Contains("GRANT INSERT, SELECT ON audit.entries", File.ReadAllText(Path.Combine(TemplateRoot, "docs/getting-started.md")), StringComparison.Ordinal);
        Assert.Contains("| Business audit | `MPCORE_BUSINESS_AUDIT_LABEL` |", File.ReadAllText(Path.Combine(TemplateRoot, "README.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_project_can_run_the_entity_framework_design_time_tools()
    {
        // `dotnet ef` resolves the context from the root service provider, together with what the
        // run-time composition attaches to it. An audited project failed with "Cannot resolve scoped
        // service ... from root provider" and could add no migration at all. The run-time
        // composition is correct, so the fix is a design-time context of its own, and this test is
        // what stops it being deleted as redundant.
        var factoryPath = Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Hosting/AppDbContextDesignTimeFactory.cs");
        Assert.True(File.Exists(factoryPath));
        var factory = File.ReadAllText(factoryPath);

        Assert.Contains("IDesignTimeDbContextFactory<AppDbContext>", factory, StringComparison.Ordinal);
        Assert.Contains("PostgreSqlDbContextOptions.Apply(options, connectionString)", factory, StringComparison.Ordinal);

        // No interceptor and no bus: the audit tables come from the model, and an interceptor that
        // only writes rows has nothing to do in a migration. Re-adding either reintroduces the gap.
        Assert.DoesNotContain("UseMPCoreAudit", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("AddMPCoreWolverine", factory, StringComparison.Ordinal);

        // The database is named on purpose. appsettings.json ships a placeholder, so a factory that
        // read configuration would point migrations at whatever that file happens to say.
        Assert.Contains("\"ConnectionStrings__PostgreSql\"", factory, StringComparison.Ordinal);
        Assert.Contains("Environment.GetEnvironmentVariable(ConnectionStringVariable)", factory, StringComparison.Ordinal);
        Assert.Contains("throw new InvalidOperationException(", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigurationBuilder", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("AddJsonFile", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("IConfiguration", factory, StringComparison.Ordinal);

        // It is generated for every backend. Request idempotency and the inbox attach an interceptor
        // as business audit does, and are added by hand: the Storefront sample's Analytics service had
        // no audit, added the inbox, and could add no migration. No condition may exclude the file.
        var excluded = JsonDocument.Parse(File.ReadAllText(Path.Combine(TemplateRoot, ".template.config/template.json")))
            .RootElement.GetProperty("sources")[0].GetProperty("modifiers").EnumerateArray()
            .SelectMany(m => m.GetProperty("exclude").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(excluded, path => path!.Contains("AppDbContextDesignTimeFactory", StringComparison.Ordinal));
        Assert.DoesNotContain("<!--#if (includeBusinessAudit) -->\nBoth commands take the database", File.ReadAllText(Path.Combine(TemplateRoot, "docs/getting-started.md")), StringComparison.Ordinal);

        // IDesignTimeDbContextFactory lives in the Design package. The reference must keep its
        // compile assets: the usual `IncludeAssets` snippet drops them and the factory stops building.
        var api = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/MPCore.Backend.Api.csproj"));
        Assert.Contains(
            "<PackageReference Include=\"Microsoft.EntityFrameworkCore.Design\" Version=\"10.0.11\" PrivateAssets=\"all\" />",
            api,
            StringComparison.Ordinal);
        Assert.DoesNotContain("IncludeAssets", api, StringComparison.Ordinal);

        // And the consumer is told, where the commands are, that the variable is required.
        var gettingStarted = File.ReadAllText(Path.Combine(TemplateRoot, "docs/getting-started.md"));
        Assert.Contains("AppDbContextDesignTimeFactory.cs", gettingStarted, StringComparison.Ordinal);
        Assert.Contains("export ConnectionStrings__PostgreSql=", gettingStarted, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_policy_refuses_credentials_and_masks_identifiers_at_the_type_level()
    {
        // The safety net is in the runtime, not only in prose: the abstractions package encodes it.
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "src/MPCore.Audit.Abstractions/AuditPolicy.cs"));
        Assert.Contains("throw new InvalidOperationException(", source, StringComparison.Ordinal);
        Assert.Contains("\"password\"", source, StringComparison.Ordinal);
        Assert.Contains("\"iban\"", source, StringComparison.Ordinal);
        Assert.Contains("public bool Required { get; internal set; } = true;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Cache_is_a_choice_with_memory_as_the_only_serverless_default()
    {
        using var template = JsonDocument.Parse(File.ReadAllText(Path.Combine(TemplateRoot, ".template.config/template.json")));
        var symbols = template.RootElement.GetProperty("symbols");
        Assert.Equal("memory", symbols.GetProperty("cache").GetProperty("defaultValue").GetString());
        Assert.Equal(new[] { "none", "memory", "redis", "hybrid" }, symbols.GetProperty("cache").GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("choice").GetString()));
        Assert.Equal("(cache == \"redis\" || cache == \"hybrid\")", symbols.GetProperty("includeCacheConnection").GetProperty("value").GetString());

        var infrastructure = Path.Combine(TemplateRoot, "src/MPCore.Backend.Infrastructure");
        var project = File.ReadAllText(Path.Combine(infrastructure, "MPCore.Backend.Infrastructure.csproj"));
        foreach (var (symbol, package) in new[] { ("includeMemoryCache", "MPCore.Caching.Memory"), ("includeRedisCache", "MPCore.Caching.Redis"), ("includeHybridCache", "MPCore.Caching.Hybrid") })
        {
            Assert.Contains($"<!--#if ({symbol}) -->\n    <PackageReference Include=\"{package}\"", project, StringComparison.Ordinal);
        }

        var registration = File.ReadAllText(Path.Combine(infrastructure, "DependencyInjection.cs"));
        Assert.Contains("//#if (includeMemoryCache)\n        services.AddMPCoreMemoryCache();\n//#elseif (includeRedisCache)\n        services.AddMPCoreRedisCache(cacheConnectionString);\n//#elseif (includeHybridCache)\n        services.AddMPCoreHybridCache(cacheConnectionString);\n//#endif", registration, StringComparison.Ordinal);

        // The connection string is required only when a server-backed cache was chosen, and the
        // generated value never carries a working credential.
        var program = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));
        Assert.Contains("//#if (includeCacheConnection)\nvar cacheConnection = builder.Configuration.GetConnectionString(\"Redis\")", program, StringComparison.Ordinal);
        var settings = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/appsettings.json"));
        Assert.Contains("\"Redis\": \"localhost:6379,abortConnect=false,password=replace-me\"", settings, StringComparison.Ordinal);

        var cli = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Program.cs"));
        Assert.Contains("\"--cache must be none, memory, redis, or hybrid.\"", cli, StringComparison.Ordinal);
        Assert.Contains("\"--cache\", cache,", cli, StringComparison.Ordinal);
        Assert.Contains("\"--cache\", cache ?? manifest.GetOrDefault(\"cache\", \"memory\")", File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Configure.cs")), StringComparison.Ordinal);

        var catalogue = File.ReadAllText(Path.Combine(TemplateRoot, "docs/capabilities.md"));
        Assert.Contains("| Hybrid adapter (in-process + Redis, stampede protection) | Supported | selected |", catalogue, StringComparison.Ordinal);
        Assert.Contains("| Invalidation policy (tags, dependencies) | **Not available** |", catalogue, StringComparison.Ordinal);
        Assert.DoesNotContain("Redis / hybrid / distributed cache | **Not available**", catalogue, StringComparison.Ordinal);
    }

    [Fact]
    public void Observability_destinations_are_configuration_and_the_scrape_endpoint_is_protected_rest_only()
    {
        var program = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));
        Assert.Contains("Signals = builder.Configuration.GetSection(\"Observability\").Get<MPCoreObservabilitySignals>()", program, StringComparison.Ordinal);

        // Prometheus pull lives on the REST listener only, is registered only when enabled, and is
        // never added to the anonymous set: the pinned AllowAnonymous receivers above do not include it.
        Assert.Contains("// #if (includeRest)\nusing MPCore.Observability.Prometheus;\n// #endif", program, StringComparison.Ordinal);
        Assert.Contains("var metricsScrapeEnabled = builder.Configuration.GetValue(\"Observability:Metrics:Prometheus:Enabled\", false);", program, StringComparison.Ordinal);
        Assert.Contains("if (metricsScrapeEnabled)\n{\n    builder.Services.AddMPCorePrometheusScrape();\n}", program, StringComparison.Ordinal);
        Assert.Contains("metricsScrape = app.MapMPCorePrometheusScrape(", program, StringComparison.Ordinal);
        Assert.DoesNotContain("metricsScrape.AllowAnonymous", program, StringComparison.Ordinal);
        Assert.DoesNotContain("metricsScrape?.AllowAnonymous", program, StringComparison.Ordinal);
        Assert.Contains("metricsScrape?.RequireListenerPort(restPort);", program, StringComparison.Ordinal);
        var mapping = program.IndexOf("metricsScrape = app.MapMPCorePrometheusScrape(", StringComparison.Ordinal);
        var restRegionEnd = program.IndexOf("// #endif", mapping, StringComparison.Ordinal);
        Assert.True(mapping > 0 && restRegionEnd > mapping, "scrape mapping must sit inside the REST endpoint region");

        var project = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/MPCore.Backend.Api.csproj"));
        var prometheus = project.IndexOf("MPCore.Observability.Prometheus", StringComparison.Ordinal);
        var restBlock = project.LastIndexOf("<!--#if (includeRest) -->", prometheus, StringComparison.Ordinal);
        var restBlockEnd = project.IndexOf("<!--#endif -->", restBlock, StringComparison.Ordinal);
        Assert.True(restBlock >= 0 && prometheus < restBlockEnd, "the Prometheus package must be referenced only with REST");

        var settings = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/appsettings.json"));
        foreach (var key in new[] { "\"Logs\": { \"Exporter\": \"None\"", "\"Metrics\": { \"Exporter\": \"None\"", "\"Traces\": { \"Exporter\": \"None\"", "\"Prometheus\": { \"Enabled\": false", "\"Redaction\": { \"Enabled\": true" })
        {
            Assert.Contains(key, settings, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("\"Headers\":", settings, StringComparison.Ordinal); // an API key never has a slot in a tracked file

        var catalogue = File.ReadAllText(Path.Combine(TemplateRoot, "docs/capabilities.md"));
        Assert.Contains("| Independent destination per signal (endpoint, protocol, headers) | Supported |", catalogue, StringComparison.Ordinal);
        Assert.Contains("| Redaction of metric labels | **Not available** |", catalogue, StringComparison.Ordinal);
        Assert.Contains("<!--#if (includeRest) -->\n| Prometheus scrape endpoint (protected by default) | Supported |", catalogue, StringComparison.Ordinal);
    }

    [Fact]
    public void Presets_are_valid_documented_and_overridable_and_the_wizard_never_hangs_a_pipeline()
    {
        var cli = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Program.cs"));
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/README.md"));
        using var template = JsonDocument.Parse(File.ReadAllText(Path.Combine(TemplateRoot, ".template.config/template.json")));
        var symbols = template.RootElement.GetProperty("symbols");
        string[] Choices(string symbol) => symbols.GetProperty(symbol).GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("choice").GetString()!).ToArray();

        // Each preset value must be a real template choice, and the README table must say the same.
        var presets = Regex.Matches(cli, "\\[\"(?<name>[a-z-]+)\"\\] = new Dictionary<string, string>\\s*\\{(?<body>[^}]*)\\}")
            .ToDictionary(m => m.Groups["name"].Value, m => Regex.Matches(m.Groups["body"].Value, "\\[\"(?<k>[a-z-]+)\"\\] = \"(?<v>[a-z-]+)\"").ToDictionary(x => x.Groups["k"].Value, x => x.Groups["v"].Value));
        Assert.Equal(new[] { "api", "modular-monolith", "service" }, presets.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (name, values) in presets)
        {
            Assert.Contains(values["transport"], Choices("transport"));
            Assert.Contains(values["shape"], Choices("shape"));
            Assert.Contains(values["messaging"], Choices("messaging"));
            Assert.Contains(values["cache"], Choices("cache"));
            Assert.Contains(values["business-audit"], Choices("businessAudit"));
            Assert.Contains($"| `{name}` | {values["transport"]} | {values["shape"]} | {values["messaging"]} | {values["cache"]} | {values["business-audit"]} |", readme, StringComparison.Ordinal);
        }

        Assert.Contains("options.TryAdd(key, value);", cli, StringComparison.Ordinal); // explicit flags win
        Assert.Contains("!Console.IsInputRedirected && MissingRequired(options)", cli, StringComparison.Ordinal);
        Assert.Contains("options.ContainsKey(\"non-interactive\")", cli, StringComparison.Ordinal);
        // The wizard asks only for these keys; none of them is a credential slot.
        var asked = Regex.Matches(cli.Substring(cli.IndexOf("RunWizard(Dictionary", StringComparison.Ordinal)), "(?:Ask|Choose)\\(options, \"(?<key>[a-z-]+)\"").Select(m => m.Groups["key"].Value).ToArray();
        Assert.Equal(new[] { "organization", "component", "output", "transport", "shape", "messaging", "cache", "business-audit", "timeseries", "ai-tooling", "security-authority", "security-audience" }, asked);
        Assert.DoesNotContain(asked, key => key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase) || key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.Contains("connection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Configure_automates_cache_and_business_audit_through_the_same_ownership_check()
    {
        var configure = File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Configure.cs"));
        Assert.Contains("PlanCapabilitySwitch(project, baseline!, final,", configure, StringComparison.Ordinal);
        Assert.Contains("MaterializeTemplateAsync(JsonNodeManifest manifest, string aiTooling, string mpcoreVersion,\n        string? cache = null, string? businessAudit = null, string? timeseries = null)", configure, StringComparison.Ordinal);
        // The switch plans updates and deletes, and both go through the conflict filter above it.
        var plan = configure.IndexOf("PlanCapabilitySwitch(project, baseline!, final,", StringComparison.Ordinal);
        var conflict = configure.IndexOf("refuse to destroy work the developer has done", StringComparison.Ordinal);
        Assert.True(plan > 0 && conflict > plan, "the capability switch must be planned before the conflict check runs");
        Assert.DoesNotContain("--force", configure, StringComparison.Ordinal);
        Assert.DoesNotContain("--keep-mine", configure, StringComparison.Ordinal);
        Assert.Contains("--cache none|memory|redis|hybrid      switch the cache adapter and its wiring", configure, StringComparison.Ordinal);
    }

    [Fact]
    public void Mutual_tls_between_services_is_opt_in_and_placed_where_it_can_see_the_proxy_and_the_endpoint()
    {
        var program = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));
        Assert.Contains("if (builder.Configuration.GetValue(\"Security:MutualTls:Enabled\", false))", program, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddMPCoreMutualTls(options => builder.Configuration.GetSection(\"Security:MutualTls\").Bind(options));", program, StringComparison.Ordinal);

        // The certificate a proxy forwards is read before gateway forwarding replaces the proxy's address, and
        // the endpoint's requirement is checked after routing and authentication, before authorization.
        var certificateForwarding = program.IndexOf("app.UseMPCoreCertificateForwarding();", StringComparison.Ordinal);
        var gatewayForwarding = program.IndexOf("app.UseMPCoreGatewayForwarding();", StringComparison.Ordinal);
        var authentication = program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        var mutualTls = program.IndexOf("app.UseMPCoreMutualTls();", StringComparison.Ordinal);
        var authorization = program.IndexOf("app.UseAuthorization();", StringComparison.Ordinal);
        Assert.True(certificateForwarding > 0 && certificateForwarding < gatewayForwarding, "a forwarded certificate is read before gateway forwarding");
        Assert.True(authentication < mutualTls && mutualTls < authorization, "the workload certificate is checked between authentication and authorization");

        var settings = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/appsettings.json"));
        Assert.Contains("\"MutualTls\": {", settings, StringComparison.Ordinal);
        Assert.Contains("\"Enabled\": false", settings, StringComparison.Ordinal);
        Assert.Contains("\"AllowedWorkloadNames\": []", settings, StringComparison.Ordinal);
        Assert.Contains("\"RevocationMode\": \"NoCheck\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void Gateway_forwarding_is_opt_in_and_first_in_the_pipeline_and_claim_mapping_is_a_named_preset()
    {
        var program = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));
        Assert.Contains("builder.Configuration.GetSection(\"Gateway:TrustedProxies\").Get<string[]>() ?? []", program, StringComparison.Ordinal);
        var forwarding = program.IndexOf("app.UseMPCoreGatewayForwarding();", StringComparison.Ordinal);
        var guard = program.IndexOf("app.UseForwardedIdentityHeaderGuard();", StringComparison.Ordinal);
        var authentication = program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        Assert.True(forwarding > 0 && forwarding < guard && guard < authentication, "forwarded headers must be applied before the identity guard and authentication");
        Assert.Contains("mapping.UseKeycloakDefaults();", program, StringComparison.Ordinal);
        Assert.Contains("mapping.UseGenericOidc();", program, StringComparison.Ordinal);

        var settings = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/appsettings.json"));
        Assert.Contains("\"TrustedProxies\": []", settings, StringComparison.Ordinal); // trust nobody until told
        Assert.Contains("\"ClaimMapping\": { \"Preset\": \"Keycloak\" }", settings, StringComparison.Ordinal);

        var catalogue = File.ReadAllText(Path.Combine(TemplateRoot, "docs/capabilities.md"));
        Assert.Contains("| Actor kind distinction (user / service / system) | Supported |", catalogue, StringComparison.Ordinal);
        Assert.DoesNotContain("| Keycloak preset | **Not available** |", catalogue, StringComparison.Ordinal);
    }

    [Fact]
    public void Timescale_is_an_opt_in_choice_with_validated_sql_helpers()
    {
        using var template = JsonDocument.Parse(File.ReadAllText(Path.Combine(TemplateRoot, ".template.config/template.json")));
        var symbol = template.RootElement.GetProperty("symbols").GetProperty("timeseries");
        Assert.Equal("none", symbol.GetProperty("defaultValue").GetString());
        Assert.Contains("<!--#if (includeTimescale) -->\n    <PackageReference Include=\"MPCore.Persistence.Timescale\"", File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Infrastructure/MPCore.Backend.Infrastructure.csproj")), StringComparison.Ordinal);
        Assert.Contains("\"--timeseries must be none or timescale.\"", File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Program.cs")), StringComparison.Ordinal);
        Assert.Contains("\"--timeseries\", timeseries ?? manifest.GetOrDefault(\"timeseries\", \"none\")", File.ReadAllText(Path.Combine(RepositoryRoot, "tools/MPCore.Cli/Configure.cs")), StringComparison.Ordinal);
        // The helpers never interpolate a caller's text into SQL without validating it first.
        var sql = File.ReadAllText(Path.Combine(RepositoryRoot, "src/MPCore.Persistence.Timescale/TimescaleSql.cs"));
        Assert.Contains("IdentifierPattern()", sql, StringComparison.Ordinal);
        Assert.Contains("IntervalPattern()", sql, StringComparison.Ordinal);
        Assert.Contains("| Continuous aggregates | **Not available** |", File.ReadAllText(Path.Combine(TemplateRoot, "docs/capabilities.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void Tenancy_resilience_and_migrations_are_wired_and_labelled_honestly()
    {
        var program = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));
        Assert.Contains("builder.Services.AddMPCoreTenancyFromClaim(builder.Configuration[\"Security:TenantClaim\"] ?? \"tenant_id\");", program, StringComparison.Ordinal);
        var api = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/MPCore.Backend.Api.csproj"));
        Assert.Contains("<PackageReference Include=\"Microsoft.EntityFrameworkCore.Design\" Version=\"10.0.11\" PrivateAssets=\"all\" />", api, StringComparison.Ordinal);
        Assert.Contains("MPCore.Tenancy.Abstractions", api, StringComparison.Ordinal);
        Assert.Contains("MPCore.Resilience.Http", File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Infrastructure/MPCore.Backend.Infrastructure.csproj")), StringComparison.Ordinal);
        Assert.Contains("\"TenantClaim\": \"tenant_id\"", File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/appsettings.json")), StringComparison.Ordinal);

        // The REST group is the version segment; the catalogue must not promise more than that.
        Assert.Contains("endpoints.MapGroup(\"/v1/platform\")", File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Rest/Endpoints/PlatformProbeEndpoints.cs")), StringComparison.Ordinal);
        var catalogue = File.ReadAllText(Path.Combine(TemplateRoot, "docs/capabilities.md"));
        Assert.Contains("| Header or media-type versioning, deprecation headers | **Not available** |", catalogue, StringComparison.Ordinal);
        Assert.Contains("| Per-tenant data partitioning or connection routing | **Not available** |", catalogue, StringComparison.Ordinal);
        Assert.Contains("| Automatic migration on startup | **Not available** |", catalogue, StringComparison.Ordinal);
        Assert.Contains("dotnet ef migrations add <Name> --project src/MPCore.Backend.Infrastructure --startup-project src/MPCore.Backend.Api", File.ReadAllText(Path.Combine(TemplateRoot, "docs/getting-started.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_application_layer_declares_ports_and_stays_provider_neutral()
    {
        var project = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Application/MPCore.Backend.Application.csproj"));

        // A handler can only declare the ports its layer can see. Without these packages the generated
        // Application cannot even name IRepository or IMessagePublisher, which is what forced consumers
        // to reach for AppDbContext instead.
        foreach (var required in new[]
                 {
                     "MPCore.Application", "MPCore.Persistence.Abstractions", "MPCore.Messaging.Abstractions",
                     "MPCore.Security.Abstractions", "MPCore.Tenancy.Abstractions",
                 })
        {
            Assert.Contains($"<PackageReference Include=\"{required}\" Version=\"$(MPCoreVersion)\" />", project, StringComparison.Ordinal);
        }

        // Everything provider-specific stays out, or the dependency direction is broken at the source.
        // Only real references count: the comment above them names those packages precisely to forbid
        // them, and a test that matched raw text would fail on its own documentation.
        var references = Regex.Matches(project, @"(?:Package|Project)Reference Include=""(?<id>[^""]+)""")
            .Select(match => match.Groups["id"].Value).ToArray();
        foreach (var forbidden in new[]
                 {
                     "Microsoft.EntityFrameworkCore", "Npgsql", "WolverineFx", "MPCore.Messaging.Wolverine",
                     "MPCore.Persistence.EntityFrameworkCore", "MPCore.Transport.", "MPCore.Security.AspNetCore",
                     "Confluent.Kafka", "RabbitMQ.Client", "MPCore.Backend.Infrastructure",
                 })
        {
            Assert.DoesNotContain(references, reference => reference.Contains(forbidden, StringComparison.Ordinal));
        }

        Assert.Contains("<ProjectReference Include=\"../MPCore.Backend.Domain/MPCore.Backend.Domain.csproj\" />", project, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(TemplateRoot, "src/MPCore.Backend.Application/AssemblyReference.cs")));
    }

    [Fact]
    public void Handler_discovery_is_explicit_and_has_one_place_that_lists_the_owners()
    {
        var program = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));
        var assemblies = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Hosting/HandlerAssemblies.cs"));

        Assert.Contains("options.DiscoverHandlersIn(HandlerAssemblies.All);", program, StringComparison.Ordinal);
        Assert.Contains("MPCore.Backend.Application.AssemblyReference.Assembly,", assemblies, StringComparison.Ordinal);

        // The list is the only discovery surface: no second call site, no scanning helper, no catch-all.
        Assert.Single(Regex.Matches(program, @"DiscoverHandlersIn\("));
        foreach (var forbidden in new[] { "Discovery.IncludeAssembly", "DiscoverHandlerModules", "CustomizeHandlerDiscovery", "PublishAllMessages" })
        {
            Assert.DoesNotContain(forbidden, program, StringComparison.Ordinal);
        }

        // A modular monolith adds its modules here, and the module guide says so in the same words.
        Assert.Contains("// #if (shape == \"modular-monolith\")", assemblies, StringComparison.Ordinal);
        Assert.Contains("AssemblyReference", File.ReadAllText(Path.Combine(TemplateRoot, "src/Modules/README.md")), StringComparison.Ordinal);
        Assert.Contains("HandlerAssemblies.cs", File.ReadAllText(Path.Combine(TemplateRoot, "src/Modules/README.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_names_one_transaction_owner_so_port_based_handlers_are_transactional()
    {
        var program = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));

        // The typed overload is the whole mechanism: without it Wolverine recognises only the concrete
        // context, and a handler that depends on IUnitOfWork is refused at code generation.
        Assert.Contains("builder.Host.UseMPCoreWolverine<AppDbContext>(", program, StringComparison.Ordinal);
        Assert.DoesNotContain("builder.Host.UseMPCoreWolverine(\n", program, StringComparison.Ordinal);
        Assert.Contains("using MPCore.Backend.Infrastructure.Persistence;", program, StringComparison.Ordinal);

        // Infrastructure registers the context through Wolverine's integration, which is also what makes
        // IUnitOfWork resolvable; a plain AddDbContext would dead-letter port-based consumers at runtime.
        var infrastructure = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Infrastructure/DependencyInjection.cs"));
        Assert.Contains("AddMPCoreWolverineDbContext<AppDbContext>", infrastructure, StringComparison.Ordinal);
        Assert.DoesNotContain("AddMPCorePostgreSql<AppDbContext>", infrastructure, StringComparison.Ordinal);

        // The handler contract in prose must match the wiring, or the next slice follows the wrong one.
        var architecture = File.ReadAllText(Path.Combine(TemplateRoot, "docs/architecture.md"));
        Assert.Contains("UseMPCoreWolverine<AppDbContext>(...)", architecture, StringComparison.Ordinal);
        Assert.Contains("it does not call `SaveChangesAsync`", architecture, StringComparison.Ordinal);
    }

    [Fact]
    public void The_failure_contract_the_docs_state_is_the_one_the_framework_enforces()
    {
        var architecture = File.ReadAllText(Path.Combine(TemplateRoot, "docs/architecture.md"));
        Assert.Contains("### Returning a failure", architecture, StringComparison.Ordinal);
        Assert.Contains("validate first, mutate second", architecture, StringComparison.Ordinal);

        // The rule lives in the runtime package, not only in prose: the policy and both middleware
        // shapes must exist, or the documented guarantee is an unbacked claim.
        var rollback = File.ReadAllText(Path.Combine(RepositoryRoot, "src/MPCore.Messaging.Wolverine/ResultFailureRollback.cs"));
        Assert.Contains("class ResultFailureRollbackPolicy : IHandlerPolicy", rollback, StringComparison.Ordinal);
        Assert.Contains("public static class ResultFailureRollback<TValue>", rollback, StringComparison.Ordinal);
        Assert.Contains("throw new ResultFailureException(result.FailureDescriptor!);", rollback, StringComparison.Ordinal);
        Assert.Contains("options.Policies.Add(new ResultFailureRollbackPolicy());",
            File.ReadAllText(Path.Combine(RepositoryRoot, "src/MPCore.Messaging.Wolverine/WolverineFoundation.cs")), StringComparison.Ordinal);

        var catalogue = File.ReadAllText(Path.Combine(TemplateRoot, "docs/capabilities.md"));
        Assert.Contains("| Failure returned after a mutation rolls the transaction back | Supported |", catalogue, StringComparison.Ordinal);
        Assert.Contains("| Automatic retry of a failed business result | **Not available** |", catalogue, StringComparison.Ordinal);
    }

    [Fact]
    public void Domain_and_integration_events_are_documented_as_different_things_and_the_contract_backs_it()
    {
        var architecture = File.ReadAllText(Path.Combine(TemplateRoot, "docs/architecture.md"));
        Assert.Contains("## Two kinds of event, deliberately not the same thing", architecture, StringComparison.Ordinal);
        Assert.Contains("after the commit of the change that raised it", architecture, StringComparison.Ordinal);
        Assert.Contains("at least once", architecture, StringComparison.Ordinal);

        // The drain seam the documentation depends on has to exist in the package, not only in prose.
        var events = File.ReadAllText(Path.Combine(RepositoryRoot, "src/MPCore.Domain/Events/DomainEvents.cs"));
        Assert.Contains("public interface IEventSource", events, StringComparison.Ordinal);
        Assert.Contains("public interface IAggregateEventSink", events, StringComparison.Ordinal);
        Assert.Contains("public sealed class NullAggregateEventSink", events, StringComparison.Ordinal);
        Assert.Contains("public abstract class AggregateRoot<TId> : Entity<TId>, IEventSource",
            File.ReadAllText(Path.Combine(RepositoryRoot, "src/MPCore.Domain/Model/Entities.cs")), StringComparison.Ordinal);

        // The old summary claimed a domain event stayed "inside the current transaction", which is the
        // opposite of what the framework does; that wording must not come back.
        Assert.DoesNotContain("stays inside the process and the current transaction", events, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_context_is_wired_to_deliver_the_events_its_aggregates_raise()
    {
        var context = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Infrastructure/Persistence/AppDbContext.cs"));
        Assert.Contains("IAggregateEventSink eventSink", context, StringComparison.Ordinal);
        Assert.Contains(": MPCoreDbContext(options, timeProvider, eventSink)", context, StringComparison.Ordinal);

        // The drain and the bridge exist in the packages, and the catalogue states both limits found
        // while proving them: an unrouted event is dropped, and a host owns one unit of work.
        Assert.Contains("DrainAggregateEventsAsync",
            File.ReadAllText(Path.Combine(RepositoryRoot, "src/MPCore.Persistence.EntityFrameworkCore.PostgreSql/EntityFrameworkPersistence.cs")), StringComparison.Ordinal);
        var bridge = File.ReadAllText(Path.Combine(RepositoryRoot, "src/MPCore.Messaging.Wolverine/AggregateEventBridge.cs"));
        Assert.Contains("public sealed class WolverineAggregateEventSink", bridge, StringComparison.Ordinal);

        var catalogue = File.ReadAllText(Path.Combine(TemplateRoot, "docs/capabilities.md"));
        Assert.Contains("| Aggregate-raised domain events delivered in-process after the commit | Supported |", catalogue, StringComparison.Ordinal);
        Assert.Contains("| Delivery of an event with no declared route | **Not available** |", catalogue, StringComparison.Ordinal);
        Assert.Contains("| A second unit-of-work context in one host | **Not available** |", catalogue, StringComparison.Ordinal);
        Assert.Contains("One unit-of-work owner per host", File.ReadAllText(Path.Combine(TemplateRoot, "docs/architecture.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_read_side_vocabulary_exists_in_the_package_the_application_layer_already_references()
    {
        // The Application project references MPCore.Application, so these types are reachable from a read
        // port without adding a package; that is the whole point of putting them there.
        var querying = Path.Combine(RepositoryRoot, "src/MPCore.Application/Querying");
        Assert.True(File.Exists(Path.Combine(querying, "Paging.cs")));
        Assert.True(File.Exists(Path.Combine(querying, "SortAllowlist.cs")));

        var paging = File.ReadAllText(Path.Combine(querying, "Paging.cs"));
        foreach (var type in new[] { "record PageRequest", "enum SortDirection", "record SortSpec", "record Page<TItem>" })
        {
            Assert.Contains(type, paging, StringComparison.Ordinal);
        }

        Assert.Contains("public const int MaximumSize = 200;", paging, StringComparison.Ordinal);

        var architecture = File.ReadAllText(Path.Combine(TemplateRoot, "docs/architecture.md"));
        Assert.Contains("## Reading", architecture, StringComparison.Ordinal);
        Assert.Contains("never leave Infrastructure", architecture, StringComparison.Ordinal);

        var catalogue = File.ReadAllText(Path.Combine(TemplateRoot, "docs/capabilities.md"));
        Assert.Contains("| Sort field allowlist with a governed validation failure | Supported |", catalogue, StringComparison.Ordinal);
        Assert.Contains("| Keyset or cursor paging | **Not available** |", catalogue, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_skills_prescribe_the_execution_model_the_framework_actually_has()
    {
        var skills = Directory.GetFiles(Path.Combine(TemplateRoot, ".mpcore/skills"), "SKILL.md", SearchOption.AllDirectories);
        Assert.NotEmpty(skills);

        foreach (var path in skills)
        {
            var body = File.ReadAllText(path);
            var name = Path.GetFileName(Path.GetDirectoryName(path)!);

            // Prescribing a DbContext or the Wolverine bus to an application handler is what sent
            // consumers down a path their own project cannot compile. Mentions are allowed only where
            // they forbid the practice or state that modules share the host's context.
            foreach (var line in body.Split('\n').Where(l => l.Contains("AppDbContext", StringComparison.Ordinal) || l.Contains("IMessageBus", StringComparison.Ordinal)))
            {
                var allowed = line.Contains("Never", StringComparison.Ordinal)
                    || line.Contains("never", StringComparison.Ordinal)
                    || line.Contains("share", StringComparison.Ordinal);
                Assert.True(allowed, $"{name}: prescribes a provider type to the Application layer: {line.Trim()}");
            }
        }

        var slice = File.ReadAllText(Path.Combine(TemplateRoot, ".mpcore/skills/mpcore-implement-vertical-slice/SKILL.md"));
        Assert.Contains("ports only", slice, StringComparison.Ordinal);
        Assert.Contains("Do not call `SaveChangesAsync`", slice, StringComparison.Ordinal);
        Assert.Contains("Validate first, mutate second.", slice, StringComparison.Ordinal);
        Assert.Contains("Hosting/HandlerAssemblies.cs", slice, StringComparison.Ordinal);

        var messaging = File.ReadAllText(Path.Combine(TemplateRoot, ".mpcore/skills/mpcore-configure-messaging/SKILL.md"));
        Assert.Contains("An event with no declared route is dropped", messaging, StringComparison.Ordinal);

        var module = File.ReadAllText(Path.Combine(TemplateRoot, ".mpcore/skills/mpcore-implement-ddd-module/SKILL.md"));
        Assert.Contains("MPCore.Persistence.Abstractions", module, StringComparison.Ordinal);
        Assert.Contains("one unit-of-work owner", module, StringComparison.Ordinal);

        // The decision behind all of it is recorded. Documentation is English (owner's rule, 2026-09-27).
        foreach (var language in new[] { "EN" })
        {
            var adr = Path.Combine(RepositoryRoot, $"docs/decisions/ADR-011-application-execution-model.{language}.md");
            Assert.True(File.Exists(adr), $"missing {adr}");
            Assert.Contains("ADR-011", File.ReadAllText(adr), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_modular_monolith_composes_one_project_per_module_and_references_no_root_application()
    {
        var api = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/MPCore.Backend.Api.csproj"));
        var infrastructure = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Infrastructure/MPCore.Backend.Infrastructure.csproj"));
        var program = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));
        var assemblies = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Hosting/HandlerAssemblies.cs"));

        // Every reference to the root Application project sits inside the service-only condition, or a
        // generated modular monolith would reference a project that was excluded.
        foreach (var (name, text, marker) in new[]
                 {
                     ("Api project", api, "<!--#if (shape == \"service\") -->"),
                     ("Infrastructure project", infrastructure, "<!--#if (shape == \"service\") -->"),
                     ("Program.cs", program, "// #if (shape == \"service\")"),
                     ("HandlerAssemblies.cs", assemblies, "// #if (shape == \"service\")"),
                 })
        {
            foreach (Match reference in Regex.Matches(text, @"MPCore\.Backend\.Application[^\n]*|AddApplication\(\)"))
            {
                var before = text[..reference.Index];
                var opened = before.LastIndexOf(marker, StringComparison.Ordinal);
                var closed = Math.Max(before.LastIndexOf("#endif", StringComparison.Ordinal), before.LastIndexOf("#else", StringComparison.Ordinal));
                Assert.True(opened > closed, $"{name}: '{reference.Value.Trim()}' is not inside the service-only condition");
            }
        }

        Assert.Contains("MPCore.Backend.Modules.Billing.AssemblyReference.Assembly", assemblies, StringComparison.Ordinal);
        Assert.DoesNotContain("Modules.Billing.Application.AssemblyReference", assemblies, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_host_renders_messages_and_validates_input_before_the_handler()
    {
        var api = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/MPCore.Backend.Api.csproj"));
        var program = File.ReadAllText(Path.Combine(TemplateRoot, "src/MPCore.Backend.Api/Program.cs"));

        Assert.Contains("<PackageReference Include=\"MPCore.Localization\" Version=\"$(MPCoreVersion)\" />", api, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"MPCore.Validation.FluentValidation\" Version=\"$(MPCoreVersion)\" />", api, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddMPCoreMessageCatalog();", program, StringComparison.Ordinal);
        Assert.Contains("options.UseMPCoreFluentValidation();", program, StringComparison.Ordinal);

        // Validators come from the same explicit list as handlers: a module that is not listed has neither.
        Assert.Matches(@"foreach \(var assembly in HandlerAssemblies\.All\)\s*\{\s*builder\.Services\.AddMPCoreValidators\(assembly\);", program);
    }

    [Fact]
    public void The_module_guide_explains_the_layout_and_names_where_each_convention_comes_from()
    {
        var guide = File.ReadAllText(Path.Combine(TemplateRoot, "src/Modules/README.md"));

        foreach (var folder in new[] { "Commands/", "Queries/", "Views/", "Ports/", "Process/", "Events/", "Validators/", "Contracts" })
        {
            Assert.Contains(folder, guide, StringComparison.Ordinal);
        }

        // What a query may do, not only where it lives: the first consumer served a command behind a GET.
        Assert.Contains("**A query only reads.**", guide, StringComparison.Ordinal);
        Assert.Contains("RFC 9110", guide, StringComparison.Ordinal);

        // A convention a developer cannot trace to its source is a convention they will argue with.
        foreach (var source in new[]
                 {
                     "Eric Evans", "Simon Brown", "package by component", "Alistair Cockburn", "Ports and Adapters",
                     "Gregor Hohpe", "Bobby Woolf", "Jimmy Bogard", "Kamil Grzybek", "Jason Taylor", "Vaughn Vernon",
                     "Vladimir Khorikov", "Jeremy Skinner", "RFC 9457",
                 })
        {
            Assert.Contains(source, guide, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_catalogue_and_the_architecture_document_describe_idempotency_as_it_is_implemented()
    {
        var catalogue = File.ReadAllText(Path.Combine(TemplateRoot, "docs/capabilities.md"));
        var architecture = File.ReadAllText(Path.Combine(TemplateRoot, "docs/architecture.md"));

        Assert.Contains("| Business-level idempotency store (deduplicate a client's repeated command) | Supported |", catalogue, StringComparison.Ordinal);
        Assert.Contains("| Replay of a failed request | **Not available** |", catalogue, StringComparison.Ordinal);
        foreach (var required in new[] { "## Idempotency", "IIdempotentExecutor", "RequireIdempotencyKey()", "UseMPCoreInbox()", "ApplyMPCoreIdempotency()", "RFC 9110", "Brandur Leach", "Gregor Hohpe" })
        {
            Assert.Contains(required, architecture, StringComparison.Ordinal);
        }

        // The runtime really has what the documents name.
        var runtime = Path.Combine(RepositoryRoot, "src");
        foreach (var marker in new[] { "interface IIdempotentExecutor", "RequireIdempotencyKey<TBuilder>", "UseMPCoreInbox(", "ApplyMPCoreIdempotency(" })
        {
            Assert.Contains(Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories), file =>
                !file.Contains("/obj/", StringComparison.Ordinal) && File.ReadAllText(file).Contains(marker, StringComparison.Ordinal));
        }
    }
}
