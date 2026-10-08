using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MPCore.Localization.EntityFrameworkCore;

/// <summary>One translation row: a key, a culture and the template an administrator wrote.</summary>
public sealed class MessageTranslationRecord
{
    /// <summary>Gets or sets the message key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the culture name, a language such as <c>en</c> or a region such as <c>en-GB</c>.</summary>
    public string Culture { get; set; } = string.Empty;

    /// <summary>Gets or sets the template.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets when the row was last written.</summary>
    public DateTimeOffset ModifiedOnUtc { get; set; }
}

/// <summary>Model configuration for the translation table. Schema <c>localization</c>, table <c>translations</c>.</summary>
public static class LocalizationModelBuilderExtensions
{
    /// <summary>Database schema of the translation table.</summary>
    public const string Schema = "localization";

    /// <summary>Name of the translation table.</summary>
    public const string Table = "translations";

    /// <summary>The largest template an administrator may store.</summary>
    public const int MaximumTextLength = 2000;

    /// <summary>
    /// Adds the translation table to the model. Call from <c>OnModelCreating</c> of the product's context,
    /// so a change commits in the handler's own transaction and the product's migrations create the table.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    public static ModelBuilder ApplyMPCoreLocalization(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<MessageTranslationRecord>(Configure);
        return modelBuilder;
    }

    private static void Configure(EntityTypeBuilder<MessageTranslationRecord> entity)
    {
        entity.ToTable(Table, Schema);
        entity.HasKey(record => new { record.Key, record.Culture });
        entity.Property(record => record.Key).HasMaxLength(160);
        entity.Property(record => record.Culture).HasMaxLength(32);
        entity.Property(record => record.Text).HasMaxLength(MaximumTextLength).IsRequired();
        entity.HasIndex(record => record.ModifiedOnUtc);
    }
}
