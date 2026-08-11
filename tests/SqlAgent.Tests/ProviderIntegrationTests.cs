using Microsoft.Data.SqlClient;
using Npgsql;
using SqlAgent.Core;
using SqlAgent.Providers.Postgres;
using SqlAgent.Providers.SqlServer;

namespace SqlAgent.Tests;

public class ProviderIntegrationTests
{
    private const string PostgresConnectionStringEnv = "SQLAGENT_TEST_POSTGRES";
    private const string SqlServerConnectionStringEnv = "SQLAGENT_TEST_SQLSERVER";

    [Fact]
    public async Task Postgres_fixture_supports_connection_test_and_query_execution()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresConnectionStringEnv);
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var provider = new PostgresProvider();
        var connection = await provider.TestConnectionAsync(connectionString);
        Assert.True(connection.Success, connection.Error);

        var result = await provider.ExecuteQueryAsync(
            connectionString,
            "SELECT 1::integer AS value",
            QueryExecutionOptions.Default);

        Assert.Equal(["value"], result.Columns);
        var row = Assert.Single(result.Rows);
        Assert.Equal(1, Convert.ToInt32(row[0]));
    }

    // A non-public schema with quoted/mixed-case identifiers, an identity column, a generated
    // STORED column, a composite PK, and a composite FK — one fixture exercising every CD-69 case.
    private const string SchemaName = "CD-69 Sales";
    private const string FixtureDdl = """
        DROP SCHEMA IF EXISTS "CD-69 Sales" CASCADE;
        CREATE SCHEMA "CD-69 Sales";

        CREATE TABLE "CD-69 Sales"."Order Item" (
            "Order Id"   integer GENERATED ALWAYS AS IDENTITY,
            "Sku"        text    NOT NULL,
            "Qty"        integer NOT NULL,
            "Line Total" integer GENERATED ALWAYS AS ("Qty" * 10) STORED,
            PRIMARY KEY ("Order Id", "Sku")
        );

        CREATE TABLE "CD-69 Sales"."Order Line" (
            "Id"       integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            "Order Id" integer NOT NULL,
            "Sku"      text    NOT NULL,
            CONSTRAINT "fk order item" FOREIGN KEY ("Order Id", "Sku")
                REFERENCES "CD-69 Sales"."Order Item" ("Order Id", "Sku")
        );
        """;

    [Fact]
    public async Task Postgres_schema_extraction_covers_non_public_quoting_identity_composite_keys()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresConnectionStringEnv);
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        try
        {
            await using (var ddl = new NpgsqlCommand(FixtureDdl, conn))
                await ddl.ExecuteNonQueryAsync();

            var schema = await new PostgresProvider().GetSchemaAsync(connectionString);

            // System catalogs are filtered out; only user schemas surface.
            Assert.DoesNotContain(schema.Tables, t =>
                t.Schema.StartsWith("pg_", StringComparison.Ordinal) || t.Schema == "information_schema");

            // Non-public schema + quoted identifiers preserved verbatim.
            var orderItem = schema.Tables.Single(t => t.Schema == SchemaName && t.Name == "Order Item");

            // Column order preserved, identity + generated STORED columns both present.
            Assert.Equal(
                ["Order Id", "Sku", "Qty", "Line Total"],
                orderItem.Columns.Select(c => c.Name));
            Assert.False(orderItem.Columns.Single(c => c.Name == "Qty").IsNullable);

            // Composite PK, in declared column order.
            Assert.Equal(["Order Id", "Sku"], orderItem.PrimaryKey);

            // Composite FK: each local column paired with its referenced column, in order — no cross product.
            var orderLine = schema.Tables.Single(t => t.Schema == SchemaName && t.Name == "Order Line");
            Assert.Equal(
                [("Order Id", "Order Id"), ("Sku", "Sku")],
                orderLine.ForeignKeys.Select(f => (f.Column, f.ReferencedColumn)));
            Assert.All(orderLine.ForeignKeys, f =>
            {
                Assert.Equal(SchemaName, f.ReferencedSchema);
                Assert.Equal("Order Item", f.ReferencedTable);
            });
        }
        finally
        {
            await using var drop = new NpgsqlCommand("DROP SCHEMA IF EXISTS \"CD-69 Sales\" CASCADE;", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task SqlServer_fixture_supports_connection_test_and_query_execution()
    {
        var connectionString = Environment.GetEnvironmentVariable(SqlServerConnectionStringEnv);
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var provider = new SqlServerProvider();
        var connection = await provider.TestConnectionAsync(connectionString);
        Assert.True(connection.Success, connection.Error);

        var result = await provider.ExecuteQueryAsync(
            connectionString,
            "SELECT CAST(1 AS int) AS value",
            QueryExecutionOptions.Default);

        Assert.Equal(["value"], result.Columns);
        var row = Assert.Single(result.Rows);
        Assert.Equal(1, Convert.ToInt32(row[0]));
    }

    [Fact]
    public async Task SqlServer_schema_extraction_covers_types_composite_keys_and_fk_order()
    {
        var connectionString = Environment.GetEnvironmentVariable(SqlServerConnectionStringEnv);
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        // Composite PK on the parent; composite FK (OrderId, Sku) on the child, plus a nullable
        // nvarchar and a decimal so length/precision/scale all have something to assert.
        const string schema = "sqlagent_ct";
        await ExecAsync(connectionString,
            $"""
            IF OBJECT_ID('{schema}.OrderLine') IS NOT NULL DROP TABLE {schema}.OrderLine;
            IF OBJECT_ID('{schema}.OrderItem') IS NOT NULL DROP TABLE {schema}.OrderItem;
            IF SCHEMA_ID('{schema}') IS NULL EXEC('CREATE SCHEMA {schema}');
            CREATE TABLE {schema}.OrderItem (
                OrderId int NOT NULL,
                Sku varchar(20) NOT NULL,
                CONSTRAINT PK_OrderItem PRIMARY KEY (OrderId, Sku));
            CREATE TABLE {schema}.OrderLine (
                LineId int NOT NULL CONSTRAINT PK_OrderLine PRIMARY KEY,
                OrderId int NOT NULL,
                Sku varchar(20) NOT NULL,
                Note nvarchar(100) NULL,
                Price decimal(18,2) NOT NULL,
                CONSTRAINT FK_OrderLine_OrderItem FOREIGN KEY (OrderId, Sku)
                    REFERENCES {schema}.OrderItem (OrderId, Sku));
            """);
        try
        {
            var db = await new SqlServerProvider().GetSchemaAsync(connectionString);

            var item = db.Tables.Single(t => t.Schema == schema && t.Name == "OrderItem");
            Assert.Equal(["OrderId", "Sku"], item.PrimaryKey); // composite PK, in declared order

            var line = db.Tables.Single(t => t.Schema == schema && t.Name == "OrderLine");

            // Composite FK comes back as ordered column pairs, each local column lined up with its target.
            Assert.Equal(
                [("OrderId", "OrderItem", "OrderId"), ("Sku", "OrderItem", "Sku")],
                line.ForeignKeys.Select(f => (f.Column, f.ReferencedTable, f.ReferencedColumn)));

            var note = line.Columns.Single(c => c.Name == "Note");
            Assert.True(note.IsNullable);
            Assert.Equal(100, note.MaxLength);

            var price = line.Columns.Single(c => c.Name == "Price");
            Assert.False(price.IsNullable);
            Assert.Equal(18, price.Precision);
            Assert.Equal(2, price.Scale);

            Assert.Equal(20, line.Columns.Single(c => c.Name == "Sku").MaxLength);
        }
        finally
        {
            await ExecAsync(connectionString,
                $"""
                IF OBJECT_ID('{schema}.OrderLine') IS NOT NULL DROP TABLE {schema}.OrderLine;
                IF OBJECT_ID('{schema}.OrderItem') IS NOT NULL DROP TABLE {schema}.OrderItem;
                IF SCHEMA_ID('{schema}') IS NOT NULL EXEC('DROP SCHEMA {schema}');
                """);
        }
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
