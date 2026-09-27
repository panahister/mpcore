using Microsoft.EntityFrameworkCore;

namespace MPCore.Idempotency.EntityFrameworkCore;

/// <summary>One used idempotency key, with the hash of its request and the value the operation returned.</summary>
public sealed class IdempotencyRecord
{
    /// <summary>Gets or sets whose key it is.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Gets or sets the key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the command the key was used for.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Gets or sets the SHA-256 of the request.</summary>
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the returned value as JSON; null for a command that returns none.</summary>
    public string? Response { get; set; }

    /// <summary>Gets or sets when the operation committed.</summary>
    public DateTimeOffset CreatedOnUtc { get; set; }
}

/// <summary>One message a consumer has processed.</summary>
public sealed class ProcessedMessageRecord
{
    /// <summary>Gets or sets the consumer.</summary>
    public string Consumer { get; set; } = string.Empty;

    /// <summary>Gets or sets the message identity.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>Gets or sets when it was processed.</summary>
    public DateTimeOffset ProcessedOnUtc { get; set; }
}

/// <summary>Model configuration. Schema <c>idempotency</c>, tables <c>requests</c> and <c>processed_messages</c>.</summary>
public static class IdempotencyModelBuilderExtensions
{
    /// <summary>Database schema of both tables.</summary>
    public const string Schema = "idempotency";

    /// <summary>
    /// Adds both tables to the model. Call from <c>OnModelCreating</c> of the product's context: the rows are
    /// written in the transaction of the business change, so they must live in the same context.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    public static ModelBuilder ApplyMPCoreIdempotency(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("requests", Schema);
            // The primary key is the guarantee: of two concurrent attempts with one key, one commit fails.
            entity.HasKey(record => new { record.Scope, record.Key });
            entity.Property(record => record.Scope).HasMaxLength(256);
            entity.Property(record => record.Key).HasMaxLength(255);
            entity.Property(record => record.Operation).HasMaxLength(512).IsRequired();
            entity.Property(record => record.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(record => record.Response).HasColumnType("jsonb");
            entity.HasIndex(record => record.CreatedOnUtc);
        });

        modelBuilder.Entity<ProcessedMessageRecord>(entity =>
        {
            entity.ToTable("processed_messages", Schema);
            entity.HasKey(record => new { record.Consumer, record.MessageId });
            entity.Property(record => record.Consumer).HasMaxLength(256);
            entity.Property(record => record.MessageId).HasMaxLength(128);
            entity.HasIndex(record => record.ProcessedOnUtc);
        });

        return modelBuilder;
    }
}
