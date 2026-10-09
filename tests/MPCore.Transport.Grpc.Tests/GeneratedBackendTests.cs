using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Grpc.Core;
using Grpc.Net.Client;

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

        // As generated there is no module. A rule that walks no module has checked nothing: each is reported as
        // skipped, not as passed, and the run still succeeds.
        var empty = await workspace.TestAsync("empty");

        Assert.True(empty.ExitCode == 0, "The generated tests fail as generated, before any module:\n" + empty.Output);
        foreach (var rule in ModuleRules)
        {
            Assert.Equal("NotExecuted", empty.Outcome(rule.Test));
        }

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

    [Fact]
    public async Task A_service_holds_its_query_rule_and_the_rule_fails_on_a_query_that_publishes()
    {
        using var workspace = await Workspace.GenerateAsync("service");
        const string Rule = "A_query_handler_takes_no_unit_of_work_and_publishes_nothing";

        var clean = await workspace.TestAsync("clean");

        Assert.True(clean.ExitCode == 0, "The generated tests fail as generated:\n" + clean.Output);
        Assert.Equal("Passed", clean.Outcome(Rule));

        workspace.Write("src/Acme.Ledger.Application/Queries/ListRecent.cs", """
            using MPCore.Application.Messaging;
            using MPCore.Messaging.Abstractions;

            namespace Acme.Ledger.Application.Queries;

            public sealed record ListRecent : IQuery<int>;

            // Seeded violation: a query whose handler can publish.
            public sealed class ListRecentHandler(IMessagePublisher publisher)
            {
                public IMessagePublisher Publisher { get; } = publisher;

                public int Handle(ListRecent query) => 0;
            }
            """);
        var seeded = await workspace.TestAsync("seeded");

        Assert.Equal("Failed", seeded.Outcome(Rule));
        Assert.Contains("ListRecentHandler.Handle takes IMessagePublisher", seeded.Message(Rule), StringComparison.Ordinal);
    }

    /// <summary>
    /// A host generated from the template logs the request of a gRPC service the host names as sensitive. Its
    /// console shows neither the value nor the names of the fields; it still shows the host's other logs. With
    /// the default logging providers brought back, which the template clears, the same call prints the request
    /// whole: the check can see the leak it guards against.
    /// </summary>
    [GeneratedHostFact]
    public async Task A_generated_host_prints_no_sensitive_request_to_its_console_and_the_default_providers_would()
    {
        var connection = Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL")!;
        using var workspace = await Workspace.GenerateAsync("service", transport: "grpc");
        workspace.SeedSensitiveService();

        var clean = await workspace.RunHostAsync(connection, Phone, Code);

        // The pipeline's console sink keeps the host's own logs on the console: silence would also be clean.
        Assert.True(clean.Contains("Application started", StringComparison.Ordinal), "The host's console shows no log:\n" + clean);
        Assert.True(clean.Contains("OtpProbeService: Verify ***", StringComparison.Ordinal), "The handler's log is not the masked request:\n" + clean);
        foreach (var leaked in new[] { Phone, Code, "\"code\"", "\"phone\"" })
        {
            Assert.False(clean.Contains(leaked, StringComparison.Ordinal), $"The console holds {leaked}:\n" + clean);
        }

        // Seeded violation: the providers ASP.NET Core adds return.
        workspace.Replace("src/Acme.Ledger.Api/Program.cs", "builder.Logging.ClearProviders();", "// Seeded violation: the default providers return.");
        var seeded = await workspace.RunHostAsync(connection, Phone, Code);

        Assert.True(seeded.Contains(Code, StringComparison.Ordinal), "With the default providers back, the console should show the request:\n" + seeded);
    }

    private const string Phone = "+1-555-0100";
    private const string Code = "552-118";

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
            "A_query_handler_takes_no_unit_of_work_and_publishes_nothing",
            "CountShipmentsHandler.Handle takes IUnitOfWork",
            static workspace => workspace.Write("src/Modules/Shipping/Acme.Ledger.Modules.Shipping/Application/Queries/CountShipments.cs", """
                using MPCore.Application.Messaging;
                using MPCore.Persistence.Abstractions;

                namespace Acme.Ledger.Modules.Shipping.Application.Queries;

                public sealed record CountShipments : IQuery<int>;

                // Seeded violation: a query that declares a unit of work could change state.
                public static class CountShipmentsHandler
                {
                    public static int Handle(CountShipments query, IUnitOfWork unitOfWork) => 0;
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
        new(
            "Each_module_maps_to_its_own_schema",
            "Acme.Ledger.Modules.Shipping maps into billing, the schema of Acme.Ledger.Modules.Billing",
            static workspace => workspace.Write("src/Modules/Shipping/Acme.Ledger.Modules.Shipping/Infrastructure/ShipmentNoteConfiguration.cs", """
                using Microsoft.EntityFrameworkCore;
                using Microsoft.EntityFrameworkCore.Metadata.Builders;

                namespace Acme.Ledger.Modules.Shipping.Infrastructure;

                public sealed class ShipmentNote
                {
                    public Guid Id { get; set; }

                    public string Text { get; set; } = string.Empty;
                }

                // Seeded violation: a module's table in another module's schema.
                public sealed class ShipmentNoteConfiguration : IEntityTypeConfiguration<ShipmentNote>
                {
                    public void Configure(EntityTypeBuilder<ShipmentNote> builder)
                    {
                        ArgumentNullException.ThrowIfNull(builder);
                        builder.ToTable("shipment_notes", "billing");
                        builder.HasKey(static note => note.Id);
                    }
                }
                """)),
        new(
            "No_foreign_key_crosses_a_schema",
            "Shipment (shipping) -> Invoice (billing)",
            static workspace => workspace.Write("src/Acme.Ledger.Infrastructure/Persistence/ShipmentInvoiceKey.cs", """
                using Acme.Ledger.Modules.Billing.Domain;
                using Acme.Ledger.Modules.Shipping.Domain;
                using Microsoft.EntityFrameworkCore;
                using Microsoft.EntityFrameworkCore.Metadata.Builders;

                namespace Acme.Ledger.Infrastructure.Persistence;

                // Seeded violation: the host, which sees both modules, ties their tables with a foreign key.
                public sealed class ShipmentInvoiceKey : IEntityTypeConfiguration<Shipment>
                {
                    public void Configure(EntityTypeBuilder<Shipment> builder)
                    {
                        ArgumentNullException.ThrowIfNull(builder);
                        builder.HasOne<Invoice>().WithMany().HasForeignKey(static shipment => shipment.InvoiceId);
                    }
                }
                """)),
        new(
            "A_contracts_interface_that_writes_declares_its_reason",
            "Acme.Ledger.Modules.Billing.Contracts.IInvoiceVoiding",
            static workspace => workspace.Write("src/Modules/Billing/Acme.Ledger.Modules.Billing.Contracts/IInvoiceVoiding.cs", """
                namespace Acme.Ledger.Modules.Billing.Contracts;

                // Seeded violation: a call that writes, published without its reason.
                public interface IInvoiceVoiding
                {
                    Task VoidAsync(Guid invoiceId, CancellationToken cancellationToken);
                }
                """)),
        new(
            "A_handler_takes_only_its_own_modules_repositories",
            "Acme.Ledger.Modules.Shipping.Application.Commands.RecallShipmentHandler takes IInvoiceRepository of Acme.Ledger.Modules.Billing",
            static workspace =>
            {
                workspace.AddProjectReference(
                    "src/Modules/Shipping/Acme.Ledger.Modules.Shipping/Acme.Ledger.Modules.Shipping.csproj",
                    "../../Billing/Acme.Ledger.Modules.Billing/Acme.Ledger.Modules.Billing.csproj");
                workspace.Write("src/Modules/Shipping/Acme.Ledger.Modules.Shipping/Application/Commands/RecallShipment.cs", """
                    using Acme.Ledger.Modules.Billing.Application.Ports;
                    using MPCore.Application.Messaging;

                    namespace Acme.Ledger.Modules.Shipping.Application.Commands;

                    public sealed record RecallShipment(Guid ShipmentId) : ICommand;

                    // Seeded violation: a handler that changes another module's aggregates.
                    public static class RecallShipmentHandler
                    {
                        public static void Handle(RecallShipment command, IInvoiceRepository invoices) =>
                            ArgumentNullException.ThrowIfNull(invoices);
                    }
                    """);
            }),
    ];

    /// <summary>A generated backend in a directory of its own, with a template home of its own.</summary>
    private sealed class Workspace : IDisposable
    {
        private readonly string _root;
        private readonly Dictionary<string, string?> _environment;
        private readonly HashSet<(string Project, string Reference)> _references = [];

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

        public static async Task<Workspace> GenerateAsync(string shape, string transport = "rest")
        {
            var workspace = new Workspace(Directory.CreateTempSubdirectory("mpcore-generated-").FullName);
            try
            {
                await workspace.RunOrThrowAsync(workspace._root, "new", "install", TemplateRoot);
                await workspace.RunOrThrowAsync(
                    workspace._root,
                    "new", "mpcore-backend", "--name", "Acme.Ledger", "--output", workspace.Backend,
                    "--organization", "Acme", "--component", "Ledger", "--shape", shape, "--messaging", "none",
                    "--transport", transport, "--businessAudit", "none", "--cache", "none", "--timeseries", "none",
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

        /// <summary>
        /// A gRPC service that logs its request, named as sensitive, and open to an anonymous caller: the
        /// authenticated fallback policy would refuse the call before the handler logged anything.
        /// </summary>
        public void SeedSensitiveService()
        {
            Write("src/Acme.Ledger.Api/Protos/otp_probe.proto", """
                syntax = "proto3";

                option csharp_namespace = "Acme.Ledger.Api.Otp";

                package acme.ledger.otp.v1;

                service OtpProbe {
                  rpc Verify (VerifyRequest) returns (VerifyReply);
                }

                message VerifyRequest {
                  string phone = 1;
                  string code = 2;
                }

                message VerifyReply {
                  string session = 1;
                }
                """);
            Write("src/Acme.Ledger.Api/Grpc/Services/OtpProbeService.cs", """
                using Acme.Ledger.Api.Otp;
                using Grpc.Core;

                namespace Acme.Ledger.Api.Grpc.Services;

                public sealed class OtpProbeService(ILogger<OtpProbeService> logger) : OtpProbe.OtpProbeBase
                {
                    public override Task<VerifyReply> Verify(VerifyRequest request, ServerCallContext context)
                    {
                        logger.LogInformation("Verify {Request}", request);
                        return Task.FromResult(new VerifyReply { Session = "session-7f3a" });
                    }
                }
                """);
            Replace(
                "src/Acme.Ledger.Api/Acme.Ledger.Api.csproj",
                "<Protobuf Include=\"Protos/platform_probe.proto\" GrpcServices=\"Server\" />",
                "<Protobuf Include=\"Protos/platform_probe.proto\" GrpcServices=\"Server\" />\n    <Protobuf Include=\"Protos/otp_probe.proto\" GrpcServices=\"Server\" />");
            Replace(
                "src/Acme.Ledger.Api/Program.cs",
                "builder.Services.AddGrpc().AddMPCoreFailureHandling();",
                "builder.Services.AddGrpc().AddMPCoreFailureHandling().AddMPCoreSensitiveMessages(\"acme.ledger.otp.v1.OtpProbe\");");
            Replace(
                "src/Acme.Ledger.Api/Program.cs",
                "var grpcProbeEndpoint = app.MapGrpcService<PlatformProbeService>();",
                "var grpcProbeEndpoint = app.MapGrpcService<PlatformProbeService>();\napp.MapGrpcService<OtpProbeService>().AllowAnonymous();");

            // The host's message store lives in a schema of its own in the test database.
            Replace("src/Acme.Ledger.Api/Program.cs", "PersistenceSchemaName = \"wolverine\"", "PersistenceSchemaName = \"wolverine_generated_host_tests\"");
        }

        /// <summary>
        /// Builds the generated host, runs it on a free loopback port, calls the seeded service once, and returns
        /// what the host wrote to its console by then.
        /// </summary>
        public async Task<string> RunHostAsync(string postgreSql, string phone, string code)
        {
            await RunOrThrowAsync(Backend, "build", "src/Acme.Ledger.Api/Acme.Ledger.Api.csproj", "--configuration", "Release", "-nodeReuse:false");
            var output = Directory.GetDirectories(Path.Combine(Backend, "src/Acme.Ledger.Api/bin/Release")).Single();
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            var start = new ProcessStartInfo("dotnet")
            {
                // The content root is the working directory: the host reads its appsettings.json from here.
                WorkingDirectory = output,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("Acme.Ledger.Api.dll");
            ApplyEnvironment(start);
            start.Environment["Kestrel__Endpoints__Grpc__Url"] = $"http://127.0.0.1:{port}";
            start.Environment["ConnectionStrings__PostgreSql"] = postgreSql;
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";

            var console = new List<string>();
            string Output()
            {
                lock (console)
                {
                    return string.Join('\n', console);
                }
            }

            async Task<bool> WaitForAsync(string text, TimeSpan patience)
            {
                var until = DateTime.UtcNow + patience;
                while (DateTime.UtcNow < until)
                {
                    if (Output().Contains(text, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    await Task.Delay(100);
                }

                return false;
            }

            using var process = new Process { StartInfo = start };
            DataReceivedEventHandler collect = (_, line) =>
            {
                if (line.Data is not null)
                {
                    lock (console)
                    {
                        console.Add(line.Data);
                    }
                }
            };
            process.OutputDataReceived += collect;
            process.ErrorDataReceived += collect;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                Assert.True(await WaitForAsync("Application started", TimeSpan.FromMinutes(2)), "The generated host did not start:\n" + Output());

                // A unary call by hand: the request is the two string fields of the seeded message.
                byte[] Field(int number, string value) => [(byte)((number << 3) | 2), (byte)Encoding.UTF8.GetByteCount(value), .. Encoding.UTF8.GetBytes(value)];
                var method = new Method<byte[], byte[]>(
                    MethodType.Unary,
                    "acme.ledger.otp.v1.OtpProbe",
                    "Verify",
                    Marshallers.Create(static bytes => bytes, static bytes => bytes),
                    Marshallers.Create(static bytes => bytes, static bytes => bytes));
                using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
                await channel.CreateCallInvoker()
                    .AsyncUnaryCall(method, null, new CallOptions(deadline: DateTime.UtcNow.AddSeconds(30)), [.. Field(1, phone), .. Field(2, code)])
                    .ResponseAsync;

                Assert.True(await WaitForAsync("OtpProbeService", TimeSpan.FromSeconds(15)), "The handler logged nothing:\n" + Output());
            }
            finally
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            return Output();
        }

        public void AddProjectReference(string project, string reference)
        {
            // Two seeded violations may need the same reference; it is added once.
            if (!_references.Add((project, reference)))
            {
                return;
            }

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

            ApplyEnvironment(start);

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

        /// <summary>
        /// The test host inherits what the outer `dotnet test` and MSBuild set, and MSBuild reads every
        /// environment variable as a property. The generated build, and the generated host, start from a clean
        /// environment instead.
        /// </summary>
        private void ApplyEnvironment(ProcessStartInfo start)
        {
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

/// <summary>Runs only when MPCORE_TEST_POSTGRESQL holds a connection string to a disposable database: the generated host stores its messages there.</summary>
public sealed class GeneratedHostFactAttribute : FactAttribute
{
    public GeneratedHostFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MPCORE_TEST_POSTGRESQL")))
        {
            Skip = "Set MPCORE_TEST_POSTGRESQL to a disposable PostgreSQL connection string to run a generated host.";
        }
    }
}
