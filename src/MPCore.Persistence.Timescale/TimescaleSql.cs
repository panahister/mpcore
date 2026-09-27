using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MPCore.Persistence.Timescale;

/// <summary>
/// SQL for TimescaleDB objects. Identifiers are validated and quoted, intervals are validated, so a
/// migration cannot smuggle arbitrary SQL through a table or column name.
/// </summary>
public static partial class TimescaleSql
{
    /// <summary>Enables the extension. Requires a role allowed to create extensions; usually run once by an administrator.</summary>
    public const string CreateExtension = "CREATE EXTENSION IF NOT EXISTS timescaledb;";

    /// <summary>Turns an existing table into a hypertable partitioned on a time column.</summary>
    /// <param name="table">Table name.</param>
    /// <param name="timeColumn">The timestamp column that partitions the data.</param>
    /// <param name="schema">Schema; null for the default.</param>
    /// <param name="chunkInterval">Chunk width as a PostgreSQL interval, for example <c>7 days</c>.</param>
    /// <param name="migrateData">Whether existing rows are moved into chunks.</param>
    public static string CreateHypertable(string table, string timeColumn, string? schema = null, string chunkInterval = "7 days", bool migrateData = true) =>
        $"SELECT create_hypertable({Literal(Qualified(schema, table))}, {Literal(ColumnName(timeColumn))}, chunk_time_interval => INTERVAL {Literal(Interval(chunkInterval))}, if_not_exists => TRUE, migrate_data => {(migrateData ? "TRUE" : "FALSE")});";

    /// <summary>Drops chunks older than the interval, on a schedule managed by TimescaleDB.</summary>
    public static string AddRetentionPolicy(string table, string olderThan, string? schema = null) =>
        $"SELECT add_retention_policy({Literal(Qualified(schema, table))}, INTERVAL {Literal(Interval(olderThan))}, if_not_exists => TRUE);";

    /// <summary>Enables compression, ordered and optionally segmented by columns.</summary>
    public static string EnableCompression(string table, string orderBy, string? segmentBy = null, string? schema = null)
    {
        var settings = $"timescaledb.compress, timescaledb.compress_orderby = {Literal(Identifier(orderBy) + " DESC")}";
        if (!string.IsNullOrWhiteSpace(segmentBy))
        {
            settings += $", timescaledb.compress_segmentby = {Literal(Identifier(segmentBy))}";
        }

        return $"ALTER TABLE {Qualified(schema, table)} SET ({settings});";
    }

    /// <summary>Compresses chunks older than the interval, on a schedule managed by TimescaleDB.</summary>
    public static string AddCompressionPolicy(string table, string olderThan, string? schema = null) =>
        $"SELECT add_compression_policy({Literal(Qualified(schema, table))}, INTERVAL {Literal(Interval(olderThan))}, if_not_exists => TRUE);";

    /// <summary>Quotes a validated identifier.</summary>
    public static string Identifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !IdentifierPattern().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not a valid identifier (letters, digits and underscores, starting with a letter or underscore, at most 63 characters).", nameof(name));
        }

        return '"' + name + '"';
    }

    // A column passed to a Timescale function is a name, not an expression: quoting it would make
    // Timescale look for a column whose name contains the quotes.
    private static string ColumnName(string name)
    {
        _ = Identifier(name);
        return name;
    }

    private static string Qualified(string? schema, string table) =>
        schema is null ? Identifier(table) : Identifier(schema) + "." + Identifier(table);

    private static string Interval(string interval)
    {
        if (string.IsNullOrWhiteSpace(interval) || !IntervalPattern().IsMatch(interval))
        {
            throw new ArgumentException($"'{interval}' is not a valid interval (a number followed by a unit such as '7 days', '1 month', '12 hours').", nameof(interval));
        }

        return interval.Trim();
    }

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^\s*\d+\s+(microsecond|millisecond|second|minute|hour|day|week|month|year)s?\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex IntervalPattern();
}

/// <summary>TimescaleDB operations as migration steps. Each one emits validated SQL through <see cref="MigrationBuilder.Sql"/>.</summary>
public static class TimescaleMigrationBuilderExtensions
{
    /// <summary>Enables the extension. Idempotent.</summary>
    public static MigrationBuilder EnsureTimescale(this MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(TimescaleSql.CreateExtension);
        return migrationBuilder;
    }

    /// <summary>Converts a table into a hypertable. Idempotent.</summary>
    public static MigrationBuilder CreateHypertable(this MigrationBuilder migrationBuilder, string table, string timeColumn, string? schema = null, string chunkInterval = "7 days", bool migrateData = true)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(TimescaleSql.CreateHypertable(table, timeColumn, schema, chunkInterval, migrateData));
        return migrationBuilder;
    }

    /// <summary>Adds a retention policy. Idempotent.</summary>
    public static MigrationBuilder AddRetentionPolicy(this MigrationBuilder migrationBuilder, string table, string olderThan, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(TimescaleSql.AddRetentionPolicy(table, olderThan, schema));
        return migrationBuilder;
    }

    /// <summary>Enables compression and adds a compression policy. Idempotent.</summary>
    public static MigrationBuilder AddCompression(this MigrationBuilder migrationBuilder, string table, string orderBy, string compressOlderThan, string? segmentBy = null, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(TimescaleSql.EnableCompression(table, orderBy, segmentBy, schema));
        migrationBuilder.Sql(TimescaleSql.AddCompressionPolicy(table, compressOlderThan, schema));
        return migrationBuilder;
    }
}
