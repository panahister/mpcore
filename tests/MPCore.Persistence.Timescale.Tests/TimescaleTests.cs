using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MPCore.Persistence.Timescale;
using Npgsql;
using Xunit;

namespace MPCore.Persistence.Timescale.Tests;

/// <summary>Runs only when MPCORE_TEST_TIMESCALE holds a connection string to a disposable TimescaleDB.</summary>
public sealed class TimescaleFactAttribute : FactAttribute
{
    public TimescaleFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MPCORE_TEST_TIMESCALE")))
        {
            Skip = "Set MPCORE_TEST_TIMESCALE to a disposable TimescaleDB connection string to run these tests.";
        }
    }
}

public class TimescaleSqlTests
{
    [Fact]
    public void Statements_quote_identifiers_and_validate_intervals()
    {
        Assert.Equal("SELECT create_hypertable('\"metrics\".\"readings\"', 'recorded_at', chunk_time_interval => INTERVAL '1 day', if_not_exists => TRUE, migrate_data => TRUE);",
            TimescaleSql.CreateHypertable("readings", "recorded_at", "metrics", "1 day"));
        Assert.Equal("SELECT add_retention_policy('\"readings\"', INTERVAL '90 days', if_not_exists => TRUE);", TimescaleSql.AddRetentionPolicy("readings", "90 days"));
        Assert.Equal("ALTER TABLE \"readings\" SET (timescaledb.compress, timescaledb.compress_orderby = '\"recorded_at\" DESC', timescaledb.compress_segmentby = '\"device_id\"');",
            TimescaleSql.EnableCompression("readings", "recorded_at", "device_id"));
    }

    [Theory]
    [InlineData("readings; DROP TABLE users; --")]
    [InlineData("1readings")]
    [InlineData("")]
    [InlineData("a\"b")]
    public void Injection_shaped_identifiers_are_refused(string name) =>
        Assert.Throws<ArgumentException>(() => TimescaleSql.CreateHypertable(name, "t"));

    [Theory]
    [InlineData("7 days'); DROP TABLE x; --")]
    [InlineData("soon")]
    [InlineData("7")]
    public void Malformed_intervals_are_refused(string interval) =>
        Assert.Throws<ArgumentException>(() => TimescaleSql.AddRetentionPolicy("readings", interval));

    [Fact]
    public void Migration_builder_extensions_emit_the_same_sql()
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        builder.EnsureTimescale().CreateHypertable("readings", "recorded_at").AddRetentionPolicy("readings", "30 days").AddCompression("readings", "recorded_at", "7 days", "device_id");
        var sql = builder.Operations.OfType<SqlOperation>().Select(o => o.Sql).ToArray();
        Assert.Equal(5, sql.Length);
        Assert.Equal(TimescaleSql.CreateExtension, sql[0]);
        Assert.Contains("create_hypertable", sql[1], StringComparison.Ordinal);
        Assert.Contains("add_compression_policy", sql[4], StringComparison.Ordinal);
    }
}

public class TimescaleIntegrationTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("MPCORE_TEST_TIMESCALE");

    [TimescaleFact]
    public async Task Hypertable_retention_and_compression_are_created_idempotently_on_a_real_timescale()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        async Task Run(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        async Task<long> Scalar(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        await Run(TimescaleSql.CreateExtension);
        await Run("DROP TABLE IF EXISTS readings");
        await Run("CREATE TABLE readings (recorded_at timestamptz NOT NULL, device_id text NOT NULL, value double precision NOT NULL)");
        foreach (var round in new[] { 1, 2 }) // second round proves idempotency
        {
            await Run(TimescaleSql.CreateHypertable("readings", "recorded_at", chunkInterval: "1 day"));
            await Run(TimescaleSql.AddRetentionPolicy("readings", "30 days"));
            await Run(TimescaleSql.EnableCompression("readings", "recorded_at", "device_id"));
            await Run(TimescaleSql.AddCompressionPolicy("readings", "7 days"));
            Assert.True(round > 0);
        }

        await Run("INSERT INTO readings SELECT now() - (g || ' hours')::interval, 'dev-' || (g % 3), g FROM generate_series(1, 200) g");
        Assert.Equal(1, await Scalar("SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'readings'"));
        Assert.Equal(200, await Scalar("SELECT count(*) FROM readings"));
        Assert.True(await Scalar("SELECT count(*) FROM timescaledb_information.chunks WHERE hypertable_name = 'readings'") > 1);
        Assert.Equal(1, await Scalar("SELECT count(*) FROM timescaledb_information.jobs WHERE proc_name = 'policy_retention' AND hypertable_name = 'readings'"));
        Assert.Equal(1, await Scalar("SELECT count(*) FROM timescaledb_information.jobs WHERE proc_name = 'policy_compression' AND hypertable_name = 'readings'"));
    }
}
