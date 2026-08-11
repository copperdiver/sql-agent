using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlAgent.Core;

namespace SqlAgent.Providers.SqlServer;

/// <summary>MS SQL Server provider. v1: connection testing only (CD-57).</summary>
public class SqlServerProvider : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => DatabaseProviderType.SqlServer;

    public async Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            return ConnectionTestResult.Ok(conn.ServerVersion, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ConnectionTestResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<DatabaseSchema> GetSchemaAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var columns = await Query(conn, ct,
            """
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE,
                   c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t
              ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                  string.Equals(r.GetString(4), "YES", StringComparison.OrdinalIgnoreCase),
                  NullableInt(r, 5), NullableInt(r, 6), NullableInt(r, 7)));

        var pks = await Query(conn, ct,
            """
            SELECT sch.name, tab.name, col.name
            FROM sys.indexes i
            JOIN sys.tables tab ON tab.object_id = i.object_id
            JOIN sys.schemas sch ON sch.schema_id = tab.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            WHERE i.is_primary_key = 1
            ORDER BY sch.name, tab.name, ic.key_ordinal
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2)));

        var fks = await Query(conn, ct,
            """
            SELECT sch.name, tab.name, col.name, rsch.name, rtab.name, rcol.name
            FROM sys.foreign_key_columns fkc
            JOIN sys.tables tab ON tab.object_id = fkc.parent_object_id
            JOIN sys.schemas sch ON sch.schema_id = tab.schema_id
            JOIN sys.columns col ON col.object_id = fkc.parent_object_id AND col.column_id = fkc.parent_column_id
            JOIN sys.tables rtab ON rtab.object_id = fkc.referenced_object_id
            JOIN sys.schemas rsch ON rsch.schema_id = rtab.schema_id
            JOIN sys.columns rcol ON rcol.object_id = fkc.referenced_object_id AND rcol.column_id = fkc.referenced_column_id
            -- constraint_object_id groups a composite FK; constraint_column_id orders its columns so
            -- each local column lines up with its referenced column (CD-68: composite FKs, FK order).
            ORDER BY sch.name, tab.name, fkc.constraint_object_id, fkc.constraint_column_id
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));

        // Non-PK indexes only (the PK is already carried separately). Skip heaps/stats (index_id 0).
        var indexes = await Query(conn, ct,
            """
            SELECT sch.name, tab.name, i.name, col.name, i.is_unique
            FROM sys.indexes i
            JOIN sys.tables tab ON tab.object_id = i.object_id
            JOIN sys.schemas sch ON sch.schema_id = tab.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            WHERE i.is_primary_key = 0 AND i.index_id > 0 AND ic.is_included_column = 0
            ORDER BY sch.name, tab.name, i.name, ic.key_ordinal
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4)));

        return SchemaModel.Build(columns, pks, fks, indexes);
    }

    public async Task<QueryResultSet> ExecuteQueryAsync(
        string connectionString, string sql, QueryExecutionOptions options, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = options.CommandTimeoutSeconds };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await ResultSetReader.ReadAsync(reader, options.MaxRows, ct);
    }

    // Catalog sizing columns are tinyint/smallint/int and often NULL; normalize to int? regardless of width.
    private static int? NullableInt(SqlDataReader r, int ordinal)
        => r.IsDBNull(ordinal) ? null : Convert.ToInt32(r.GetValue(ordinal));

    private static async Task<List<T>> Query<T>(
        SqlConnection conn, CancellationToken ct, string sql, Func<SqlDataReader, T> map)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
            rows.Add(map(reader));
        return rows;
    }
}
