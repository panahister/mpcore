namespace MPCore.Transport.Grpc.Tests;

/// <summary>
/// Two small modules written the way the generated module guide prescribes: Billing, which publishes a
/// Contracts project with one reading interface, and Shipping, which reads Billing only through it. Every
/// rule of the generated tests holds for them; each seeded violation breaks one rule.
/// </summary>
internal static class SeededModules
{
    private const string BillingRoot = "src/Modules/Billing/Acme.Ledger.Modules.Billing";
    private const string BillingContracts = "src/Modules/Billing/Acme.Ledger.Modules.Billing.Contracts";
    private const string ShippingRoot = "src/Modules/Shipping/Acme.Ledger.Modules.Shipping";

    private const string ModuleProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <ItemGroup>
            <PackageReference Include="MPCore.Domain" Version="$(MPCoreVersion)" />
            <PackageReference Include="MPCore.Application" Version="$(MPCoreVersion)" />
            <PackageReference Include="MPCore.Persistence.Abstractions" Version="$(MPCoreVersion)" />
            <PackageReference Include="MPCore.Messaging.Abstractions" Version="$(MPCoreVersion)" />
            <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.11" />
            <ProjectReference Include="../../Billing/Acme.Ledger.Modules.Billing.Contracts/Acme.Ledger.Modules.Billing.Contracts.csproj" />
          </ItemGroup>
        </Project>
        """;

    public static IReadOnlyDictionary<string, string> Files { get; } = new Dictionary<string, string>
    {
        [$"{BillingContracts}/Acme.Ledger.Modules.Billing.Contracts.csproj"] = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="MPCore.Application" Version="$(MPCoreVersion)" />
              </ItemGroup>
            </Project>
            """,
        [$"{BillingContracts}/IInvoiceLookup.cs"] = """
            namespace Acme.Ledger.Modules.Billing.Contracts;

            /// <summary>What other modules may read about an invoice.</summary>
            public interface IInvoiceLookup
            {
                Task<decimal?> GetAmountAsync(Guid invoiceId, CancellationToken cancellationToken);
            }
            """,

        [$"{BillingRoot}/Acme.Ledger.Modules.Billing.csproj"] = ModuleProject,
        [$"{BillingRoot}/AssemblyReference.cs"] = """
            namespace Acme.Ledger.Modules.Billing;

            public static class AssemblyReference
            {
                public static System.Reflection.Assembly Assembly => typeof(AssemblyReference).Assembly;
            }
            """,
        [$"{BillingRoot}/Domain/Invoice.cs"] = """
            namespace Acme.Ledger.Modules.Billing.Domain;

            public sealed class Invoice
            {
                public Guid Id { get; set; }

                public decimal Amount { get; set; }

                public List<InvoiceLine> Lines { get; } = [];
            }

            public sealed class InvoiceLine
            {
                public Guid Id { get; set; }

                public Guid InvoiceId { get; set; }

                public string Description { get; set; } = string.Empty;
            }
            """,
        [$"{BillingRoot}/Application/Ports/IInvoiceRepository.cs"] = """
            using Acme.Ledger.Modules.Billing.Domain;

            namespace Acme.Ledger.Modules.Billing.Application.Ports;

            public interface IInvoiceRepository
            {
                void Add(Invoice invoice);
            }
            """,
        [$"{BillingRoot}/Application/Ports/IInvoiceReadModel.cs"] = """
            using Acme.Ledger.Modules.Billing.Application.Views;

            namespace Acme.Ledger.Modules.Billing.Application.Ports;

            public interface IInvoiceReadModel
            {
                Task<InvoiceView?> FindAsync(Guid id, CancellationToken cancellationToken);
            }
            """,
        [$"{BillingRoot}/Application/Views/InvoiceView.cs"] = """
            namespace Acme.Ledger.Modules.Billing.Application.Views;

            public sealed record InvoiceView(Guid Id, decimal Amount);
            """,
        [$"{BillingRoot}/Application/Commands/IssueInvoice.cs"] = """
            using Acme.Ledger.Modules.Billing.Application.Ports;
            using Acme.Ledger.Modules.Billing.Domain;
            using MPCore.Application.Messaging;
            using MPCore.Persistence.Abstractions;

            namespace Acme.Ledger.Modules.Billing.Application.Commands;

            public sealed record IssueInvoice(Guid Id, decimal Amount) : ICommand;

            public static class IssueInvoiceHandler
            {
                public static void Handle(IssueInvoice command, IInvoiceRepository invoices, IUnitOfWork unitOfWork)
                {
                    ArgumentNullException.ThrowIfNull(command);
                    ArgumentNullException.ThrowIfNull(invoices);
                    invoices.Add(new Invoice { Id = command.Id, Amount = command.Amount });
                }
            }
            """,
        [$"{BillingRoot}/Application/Queries/GetInvoice.cs"] = """
            using Acme.Ledger.Modules.Billing.Application.Ports;
            using Acme.Ledger.Modules.Billing.Application.Views;
            using MPCore.Application.Messaging;

            namespace Acme.Ledger.Modules.Billing.Application.Queries;

            public sealed record GetInvoice(Guid Id) : IQuery<InvoiceView?>;

            public static class GetInvoiceHandler
            {
                public static Task<InvoiceView?> Handle(GetInvoice query, IInvoiceReadModel invoices, CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(query);
                    ArgumentNullException.ThrowIfNull(invoices);
                    return invoices.FindAsync(query.Id, cancellationToken);
                }
            }
            """,
        [$"{BillingRoot}/Application/Contracts/InvoiceLookup.cs"] = """
            using Acme.Ledger.Modules.Billing.Application.Ports;
            using Acme.Ledger.Modules.Billing.Contracts;

            namespace Acme.Ledger.Modules.Billing.Application.Contracts;

            public sealed class InvoiceLookup(IInvoiceReadModel invoices) : IInvoiceLookup
            {
                public async Task<decimal?> GetAmountAsync(Guid invoiceId, CancellationToken cancellationToken) =>
                    (await invoices.FindAsync(invoiceId, cancellationToken).ConfigureAwait(false))?.Amount;
            }
            """,
        [$"{BillingRoot}/Infrastructure/InvoiceConfiguration.cs"] = """
            using Acme.Ledger.Modules.Billing.Domain;
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Acme.Ledger.Modules.Billing.Infrastructure;

            public sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
            {
                public void Configure(EntityTypeBuilder<Invoice> builder)
                {
                    ArgumentNullException.ThrowIfNull(builder);
                    builder.ToTable("invoices", "billing");
                    builder.HasKey(static invoice => invoice.Id);
                    builder.HasMany(static invoice => invoice.Lines).WithOne().HasForeignKey(static line => line.InvoiceId);
                }
            }

            public sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
            {
                public void Configure(EntityTypeBuilder<InvoiceLine> builder)
                {
                    ArgumentNullException.ThrowIfNull(builder);
                    builder.ToTable("invoice_lines", "billing");
                    builder.HasKey(static line => line.Id);
                }
            }
            """,

        [$"{ShippingRoot}/Acme.Ledger.Modules.Shipping.csproj"] = ModuleProject,
        [$"{ShippingRoot}/AssemblyReference.cs"] = """
            namespace Acme.Ledger.Modules.Shipping;

            public static class AssemblyReference
            {
                public static System.Reflection.Assembly Assembly => typeof(AssemblyReference).Assembly;
            }
            """,
        [$"{ShippingRoot}/Domain/Shipment.cs"] = """
            namespace Acme.Ledger.Modules.Shipping.Domain;

            public sealed class Shipment
            {
                public Guid Id { get; set; }

                public Guid InvoiceId { get; set; }

                public decimal DeclaredValue { get; set; }
            }
            """,
        [$"{ShippingRoot}/Application/Ports/IShipmentRepository.cs"] = """
            using Acme.Ledger.Modules.Shipping.Domain;

            namespace Acme.Ledger.Modules.Shipping.Application.Ports;

            public interface IShipmentRepository
            {
                void Add(Shipment shipment);
            }
            """,
        [$"{ShippingRoot}/Application/Commands/ShipInvoice.cs"] = """
            using Acme.Ledger.Modules.Billing.Contracts;
            using Acme.Ledger.Modules.Shipping.Application.Ports;
            using Acme.Ledger.Modules.Shipping.Domain;
            using MPCore.Application.Messaging;
            using MPCore.Persistence.Abstractions;

            namespace Acme.Ledger.Modules.Shipping.Application.Commands;

            public sealed record ShipInvoice(Guid ShipmentId, Guid InvoiceId) : ICommand;

            public static class ShipInvoiceHandler
            {
                public static async Task Handle(
                    ShipInvoice command,
                    IInvoiceLookup invoices,
                    IShipmentRepository shipments,
                    IUnitOfWork unitOfWork,
                    CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(command);
                    ArgumentNullException.ThrowIfNull(invoices);
                    ArgumentNullException.ThrowIfNull(shipments);
                    var amount = await invoices.GetAmountAsync(command.InvoiceId, cancellationToken).ConfigureAwait(false);
                    shipments.Add(new Shipment { Id = command.ShipmentId, InvoiceId = command.InvoiceId, DeclaredValue = amount ?? 0m });
                }
            }
            """,
        [$"{ShippingRoot}/Infrastructure/ShipmentConfiguration.cs"] = """
            using Acme.Ledger.Modules.Shipping.Domain;
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Acme.Ledger.Modules.Shipping.Infrastructure;

            public sealed class ShipmentConfiguration : IEntityTypeConfiguration<Shipment>
            {
                public void Configure(EntityTypeBuilder<Shipment> builder)
                {
                    ArgumentNullException.ThrowIfNull(builder);
                    builder.ToTable("shipments", "shipping");
                    builder.HasKey(static shipment => shipment.Id);
                }
            }
            """,
    };
}
