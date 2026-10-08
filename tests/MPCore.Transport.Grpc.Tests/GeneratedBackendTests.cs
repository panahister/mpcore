using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MPCore.Transport.Grpc.Tests;

/// <summary>
/// The rules a generated backend carries are run, not read: a backend is generated from this repository's
/// template, built against this repository's source, given real modules, and its own tests are run twice:
/// as generated, when every rule holds, and with a violation seeded for each rule, when each rule fails for
/// its own reason. A rule that has only ever been seen passing proves nothing.
/// </summary>
/// <remarks>
/// The template is installed into a template home created for this run and removed with it. MP Core is
/// consumed as project references to <c>src</c>, so no MP Core package is packed, restored or cached.
/// Third-party packages come from the machine's NuGet cache or feed, as for any build.
/// </remarks>
[Trait("Category", "Generated")]
public sealed partial class GeneratedBackendTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string TemplateRoot = Path.Combine(RepositoryRoot, "tools/MPCore.Templates/content/MPCore.Backend");
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(8);

    [Fact]
    public async Task A_modular_monolith_holds_its_module_rules_and_each_rule_fails_on_its_seeded_violation()
    {
        using var workspace = await Workspace.GenerateAsync("modular-monolith");
        workspace.SeedModules();

        var clean = await workspace.TestAsync("clean");

        Assert.True(clean.ExitCode == 0, "The generated tests fail as generated:\n" + clean.Output);
        foreach (var rule in ModuleRules)
        {
            Assert.Equal("Passed", clean.Outcome(rule.Test));
        }

        foreach (var rule in ModuleRules)
        {
            rule.Seed(workspace);
        }

        var seeded = await workspace.TestAsync("seeded");

        Assert.NotEqual(0, seeded.ExitCode);
        foreach (var rule in ModuleRules)
        {
            Assert.Equal("Failed", seeded.Outcome(rule.Test));
            Assert.Contains(rule.Evidence, seeded.Message(rule.Test), StringComparison.Ordinal);
        }
    }

    /// <summary>A rule of the generated tests, the violation that must break it, and what its failure names.</summary>
    private sealed record Rule(string Test, string Evidence, Action<Workspace> Seed);

    private static readonly Rule[] ModuleRules =
    [
        new(
            "Each_modules_domain_knows_neither_its_other_layers_nor_a_provider",
            "Acme.Ledger.Modules.Billing.Domain.InvoiceAudit",
            static workspace => workspace.Write("src/Modules/Billing/Acme.Ledger.Modules.Billing/Domain/InvoiceAudit.cs", """
                using Acme.Ledger.Modules.Billing.Application.Ports;

                namespace Acme.Ledger.Modules.Billing.Domain;

                // Seeded violation: the domain reaches into its own application layer.
                public sealed class InvoiceAudit(IInvoiceRepository invoices)
                {
                    public IInvoiceRepository Invoices { get; } = invoices;
                }
                """)),
        new(
            "Each_modules_application_knows_neither_its_infrastructure_nor_a_provider",
            "Acme.Ledger.Modules.Billing.Application.Ports.InvoiceMappingProbe",
            static workspace => workspace.Write("src/Modules/Billing/Acme.Ledger.Modules.Billing/Application/Ports/InvoiceMappingProbe.cs", """
                using Acme.Ledger.Modules.Billing.Infrastructure;

                namespace Acme.Ledger.Modules.Billing.Application.Ports;

                // Seeded violation: the application layer depends on its module's infrastructure.
                public sealed class InvoiceMappingProbe
                {
                    public InvoiceConfiguration Configuration { get; } = new();
                }
                """)),
        new(
            "No_module_references_another_modules_main_project",
            "Acme.Ledger.Modules.Shipping -> Acme.Ledger.Modules.Billing",
            static workspace =>
            {
                workspace.AddProjectReference(
                    "src/Modules/Shipping/Acme.Ledger.Modules.Shipping/Acme.Ledger.Modules.Shipping.csproj",
                    "../../Billing/Acme.Ledger.Modules.Billing/Acme.Ledger.Modules.Billing.csproj");
                workspace.Write("src/Modules/Shipping/Acme.Ledger.Modules.Shipping/Application/Ports/BillingShortcut.cs", """
                    namespace Acme.Ledger.Modules.Shipping.Application.Ports;

                    // Seeded violation: another module's main project instead of its Contracts.
                    public sealed class BillingShortcut
                    {
                        public System.Type Invoice { get; } = typeof(Acme.Ledger.Modules.Billing.Domain.Invoice);
                    }
                    """);
            }),
    ];

    /// <summary>A generated backend in a directory of its own, with a template home of its own.</summary>
    private sealed class Workspace : IDisposable
    {
        private readonly string _root;
        private readonly Dictionary<string, string?> _environment;

        private Workspace(string root)
        {
            _root = root;
            Backend = Path.Combine(root, "Ledger");
            _environment = new Dictionary<string, string?>
            {
                ["DOTNET_CLI_HOME"] = Path.Combine(root, "home"),
                ["DOTNET_NOLOGO"] = "true",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["MSBUILDDISABLENODEREUSE"] = "1",

                // A template home of its own must not move the package cache with it: the SDK would then
                // restore everything again, and restore this repository's projects against that cache.
                // Only third-party packages are restored here; MP Core is referenced as projects.
                ["NUGET_PACKAGES"] = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
            };
        }

        public string Backend { get; }

        public static async Task<Workspace> GenerateAsync(string shape)
        {
            var workspace = new Workspace(Directory.CreateTempSubdirectory("mpcore-generated-").FullName);
            try
            {
                await workspace.RunOrThrowAsync(workspace._root, "new", "install", TemplateRoot);
                await workspace.RunOrThrowAsync(
                    workspace._root,
                    "new", "mpcore-backend", "--name", "Acme.Ledger", "--output", workspace.Backend,
                    "--organization", "Acme", "--component", "Ledger", "--shape", shape, "--messaging", "none",
                    "--transport", "rest", "--businessAudit", "none", "--cache", "none", "--timeseries", "none",
                    "--aiTooling", "none", "--securityAuthority", "https://identity.invalid/realms/replace-me",
                    "--securityAudience", "replace-me", "--mpcoreVersion", CohortVersion());
                workspace.UseMPCoreSource();
                return workspace;
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Backend, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void AddProjectReference(string project, string reference)
        {
            var path = Path.Combine(Backend, project);
            var text = File.ReadAllText(path);
            File.WriteAllText(path, text.Replace(
                "</Project>",
                $"  <ItemGroup>\n    <ProjectReference Include=\"{reference}\" />\n  </ItemGroup>\n</Project>",
                StringComparison.Ordinal));
        }

        public void Replace(string relativePath, string oldText, string newText)
        {
            var path = Path.Combine(Backend, relativePath);
            var text = File.ReadAllText(path);
            Assert.Contains(oldText, text, StringComparison.Ordinal);
            File.WriteAllText(path, text.Replace(oldText, newText, StringComparison.Ordinal));
        }

        /// <summary>Two modules, Billing with a Contracts project and Shipping, registered as the module guide says.</summary>
        public void SeedModules()
        {
            foreach (var (path, content) in SeededModules.Files)
            {
                Write(path, content);
            }

            foreach (var module in new[] { "Billing", "Shipping" })
            {
                var project = $"../Modules/{module}/Acme.Ledger.Modules.{module}/Acme.Ledger.Modules.{module}.csproj";
                AddProjectReference("src/Acme.Ledger.Api/Acme.Ledger.Api.csproj", project);
                AddProjectReference("src/Acme.Ledger.Infrastructure/Acme.Ledger.Infrastructure.csproj", project);
            }

            Replace(
                "src/Acme.Ledger.Api/Hosting/HandlerAssemblies.cs",
                "    ];",
                "        Acme.Ledger.Modules.Billing.AssemblyReference.Assembly,\n        Acme.Ledger.Modules.Shipping.AssemblyReference.Assembly,\n    ];");
            Replace(
                "src/Acme.Ledger.Infrastructure/Persistence/AppDbContext.cs",
                "        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);",
                "        modelBuilder.ApplyConfigurationsFromAssembly(Acme.Ledger.Modules.Billing.AssemblyReference.Assembly);\n" +
                "        modelBuilder.ApplyConfigurationsFromAssembly(Acme.Ledger.Modules.Shipping.AssemblyReference.Assembly);\n" +
                "        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);");
        }

        public async Task<TestRun> TestAsync(string label)
        {
            var results = Path.Combine(_root, "results", label);
            var (exitCode, output) = await RunAsync(
                Backend,
                // Relative to the working directory: on macOS the temporary directory is reached through the
                // /var symbolic link, and a project named by that path does not match the same project reached
                // through /private/var in the restore graph, which then drops the test project's references.
                "test", "tests/Acme.Ledger.Tests/Acme.Ledger.Tests.csproj", "--configuration", "Release", "--results-directory", results,
                "--logger", "trx;LogFileName=results.trx", "-nodeReuse:false");
            var trx = Directory.Exists(results) ? Directory.GetFiles(results, "*.trx", SearchOption.AllDirectories) : [];
            return new TestRun(exitCode, output, trx.Length == 1 ? XDocument.Load(trx[0]) : null);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A build server can hold a file a moment longer; the system removes temporary files.
            }
        }

        /// <summary>
        /// Every MP Core package the generated projects reference becomes a reference to its project in this
        /// repository, so the backend is built against this source and no MP Core package is restored.
        /// </summary>
        private void UseMPCoreSource()
        {
            var source = Path.Combine(RepositoryRoot, "src");
            var swaps = new StringBuilder("<Project>\n");
            foreach (var project in Directory.GetDirectories(source, "MPCore.*").Select(Path.GetFileName).Order(StringComparer.Ordinal))
            {
                var csproj = Path.Combine(source, project!, project + ".csproj");
                if (!File.Exists(csproj))
                {
                    continue;
                }

                swaps.Append($"""
                      <ItemGroup Condition="'@(PackageReference->WithMetadataValue('Identity','{project}'))' != ''">
                        <PackageReference Remove="{project}" />
                        <ProjectReference Include="{csproj}" />
                      </ItemGroup>

                    """);
            }

            swaps.Append("</Project>\n");
            File.WriteAllText(Path.Combine(Backend, "Directory.Build.targets"), swaps.ToString());
        }

        private async Task RunOrThrowAsync(string directory, params string[] arguments)
        {
            var (exitCode, output) = await RunAsync(directory, arguments);
            Assert.True(exitCode == 0, $"dotnet {string.Join(' ', arguments)} failed:\n{output}");
        }

        private async Task<(int ExitCode, string Output)> RunAsync(string directory, params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            // The test host inherits what the outer `dotnet test` and MSBuild set, and MSBuild reads every
            // environment variable as a property. The generated build starts from a clean environment instead.
            var inherited = start.Environment.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            start.Environment.Clear();
            foreach (var name in new[] { "PATH", "HOME", "USER", "LOGNAME", "TMPDIR", "TEMP", "TMP", "LANG", "LC_ALL", "SHELL", "DOTNET_ROOT" })
            {
                if (inherited.TryGetValue(name, out var value) && value is not null)
                {
                    start.Environment[name] = value;
                }
            }

            foreach (var (name, value) in _environment)
            {
                start.Environment[name] = value;
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(Patience);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"dotnet {string.Join(' ', arguments)} did not finish within {Patience}.");
            }

            return (process.ExitCode, await output + await error);
        }

        private static string CohortVersion() =>
            VersionPrefix().Match(File.ReadAllText(Path.Combine(RepositoryRoot, "src/Directory.Build.props"))).Groups[1].Value;
    }

    /// <summary>The outcome of one run of the generated tests, read from its TRX report.</summary>
    private sealed record TestRun(int ExitCode, string Output, XDocument? Report)
    {
        private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

        public string Outcome(string test) => Result(test)?.Attribute("outcome")?.Value ?? "(not run)\n" + Output;

        public string Message(string test) => Result(test)?.Descendants(Trx + "Message").FirstOrDefault()?.Value ?? string.Empty;

        private XElement? Result(string test) =>
            Report?.Descendants(Trx + "UnitTestResult")
                .FirstOrDefault(result => result.Attribute("testName")?.Value.EndsWith("." + test, StringComparison.Ordinal) == true);
    }

    [GeneratedRegex("<VersionPrefix>([^<]+)</VersionPrefix>")]
    private static partial Regex VersionPrefix();
}
