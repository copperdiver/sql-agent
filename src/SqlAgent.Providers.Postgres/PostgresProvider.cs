using System.Diagnostics;
using Npgsql;
using SqlAgent.Core;

namespace SqlAgent.Providers.Postgres;

/// <summary>PostgreSQL provider (Npgsql). v1: connection testing only (CD-57).</summary>
public class PostgresProvider : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => DatabaseProviderType.Postgres;

    public async Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            return ConnectionTestResult.Ok(conn.PostgreSqlVersion.ToString(), sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ConnectionTestResult.Fail(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<DatabaseSchema> GetSchemaAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // System schemas are noise to the LLM; only describe user tables. The 'pg_' prefix is
        // reserved for system schemas (pg_catalog, pg_toast, pg_temp_*), so excluding it plus
        // information_schema hides every catalog/temp object while keeping all user schemas.
        const string userSchemas = @"c.table_schema NOT LIKE 'pg\_%' AND c.table_schema <> 'information_schema'";

        var columns = await Query(conn, ct,
            $"""
            SELECT c.table_schema, c.table_name, c.column_name, c.data_type, c.is_nullable,
                   c.character_maximum_length, c.numeric_precision, c.numeric_scale
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE t.table_type = 'BASE TABLE' AND {userSchemas}
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                  string.Equals(r.GetString(4), "YES", StringComparison.OrdinalIgnoreCase),
                  NullableInt(r, 5), NullableInt(r, 6), NullableInt(r, 7)));

        var pks = await Query(conn, ct,
            """
            SELECT tc.table_schema, tc.table_name, kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON kcu.constraint_name = tc.constraint_name AND kcu.constraint_schema = tc.constraint_schema
            WHERE tc.constraint_type = 'PRIMARY KEY'
              AND tc.table_schema NOT LIKE 'pg\_%' AND tc.table_schema <> 'information_schema'
            ORDER BY tc.table_schema, tc.table_name, kcu.ordinal_position
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2)));

        var fks = await Query(conn, ct,
            // pg_catalog, not information_schema: unnesting conkey/confkey together pairs each local
            // FK column with its referenced column in order. The information_schema join only matches
            // on constraint name, which cross-products the columns of a composite FK.
            """
            SELECT ns.nspname, cl.relname, att.attname,
                   fns.nspname, fcl.relname, fatt.attname
            FROM pg_constraint con
            JOIN pg_class cl ON cl.oid = con.conrelid
            JOIN pg_namespace ns ON ns.oid = cl.relnamespace
            JOIN pg_class fcl ON fcl.oid = con.confrelid
            JOIN pg_namespace fns ON fns.oid = fcl.relnamespace
            JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS k(local_attnum, ref_attnum, ord) ON true
            JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = k.local_attnum
            JOIN pg_attribute fatt ON fatt.attrelid = con.confrelid AND fatt.attnum = k.ref_attnum
            WHERE con.contype = 'f'
              AND ns.nspname NOT LIKE 'pg\_%' AND ns.nspname <> 'information_schema'
            ORDER BY ns.nspname, cl.relname, con.conname, k.ord
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)));

        // Non-PK indexes only (the PK is carried separately). Expression-index parts (attnum 0) drop out of
        // the att join, so a purely expression index simply contributes no columns here.
        var indexes = await Query(conn, ct,
            """
            SELECT ns.nspname, tab.relname, idx.relname, att.attname, ix.indisunique
            FROM pg_index ix
            JOIN pg_class idx ON idx.oid = ix.indexrelid
            JOIN pg_class tab ON tab.oid = ix.indrelid
            JOIN pg_namespace ns ON ns.oid = tab.relnamespace
            JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
            JOIN pg_attribute att ON att.attrelid = tab.oid AND att.attnum = k.attnum
            WHERE ix.indisprimary = false
              AND att.attnum > 0
              AND ns.nspname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY ns.nspname, tab.relname, idx.relname, k.ord
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4)));

        return SchemaModel.Build(columns, pks, fks, indexes);
    }

    public async Task<QueryResultSet> ExecuteQueryAsync(
        string connectionString, string sql, QueryExecutionOptions options, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = options.CommandTimeoutSeconds };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await ResultSetReader.ReadAsync(reader, options.MaxRows, ct);
    }

    // information_schema sizing columns are int/bigint and often NULL; normalize to int? regardless of width.
    private static int? NullableInt(NpgsqlDataReader r, int ordinal)
        => r.IsDBNull(ordinal) ? null : Convert.ToInt32(r.GetValue(ordinal));

    private static async Task<List<T>> Query<T>(
        NpgsqlConnection conn, CancellationToken ct, string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
            rows.Add(map(reader));
        return rows;
    }
}
