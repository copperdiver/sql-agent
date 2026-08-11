using System.Text.Json.Serialization;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Api.Mcp;

// CD-50 T7 MCP tool contract. These records ARE the wire contract plugin authors see: every field is
// pinned to a stable snake_case JSON name so the shape never drifts with serializer defaults. Tool
// outcomes always carry the same shape — `ok` plus, on failure, a stable `error_code` (see the codes in
// McpToolService) — so a plugin branches on one field instead of parsing prose.

public record DatabaseSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("read_only")] bool ReadOnly);

public record ListDatabasesResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("databases")] IReadOnlyList<DatabaseSummary> Databases,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null);

/// <summary>The local-access token this MCP process was started with (from the SQLAGENT_AUTH_TOKEN env var,
/// the standard way an MCP stdio client passes a secret). Null when none was provided.</summary>
public sealed record McpClientToken(string? Value);

public record ColumnDescription(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("data_type")] string DataType,
    [property: JsonPropertyName("nullable")] bool Nullable);

public record ForeignKeyDescription(
    [property: JsonPropertyName("column")] string Column,
    [property: JsonPropertyName("references_schema")] string ReferencesSchema,
    [property: JsonPropertyName("references_table")] string ReferencesTable,
    [property: JsonPropertyName("references_column")] string ReferencesColumn);

public record TableDescription(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("columns")] IReadOnlyList<ColumnDescription> Columns,
    [property: JsonPropertyName("primary_key")] IReadOnlyList<string> PrimaryKey,
    [property: JsonPropertyName("foreign_keys")] IReadOnlyList<ForeignKeyDescription> ForeignKeys);

public record DescribeSchemaResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("database_id")] string? DatabaseId = null,
    [property: JsonPropertyName("tables")] IReadOnlyList<TableDescription>? Tables = null,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null);

public record QueryDatabaseResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("columns")] IReadOnlyList<string> Columns,
    [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyList<object?>> Rows,
    [property: JsonPropertyName("row_count")] int RowCount,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null);

/// <summary>
/// Transport-free implementation of the three CD-50 database MCP tools. Each method maps the Core
/// services (config, T4 schema description, T6 validated execution) onto the stable contract above and
/// surfaces stable error codes instead of throwing — so the MCP wrapper (DatabaseTools) stays a one-liner
/// and the behavior is unit-testable without a live MCP client or database server.
/// </summary>
/// <remarks>
/// Error codes: <c>invalid_database_id</c>, <c>connection_not_found</c>, <c>connection_secret_missing</c>,
/// <c>schema_extraction_error</c> (describe_schema), and — passed through from T6 query validation/execution —
/// <c>policy_denied_readonly</c>, <c>policy_denied_hidden_table</c>, <c>execution_timeout</c>,
/// <c>execution_canceled</c>, <c>execution_error</c>.
/// </remarks>
public class McpToolService(
    DatabaseConnectionService connections,
    SchemaService schemas,
    QueryExecutionService executor,
    LocalTokenAuthenticator authenticator,
    McpClientToken clientToken)
{
    // Every tool authenticates the process-level client token (CD-51 Story 1.7) before doing any work; when
    // no token is configured the gate passes through. ponytail: stdio MCP authenticates once per process via
    // the launch env, not per call — fine for a local single-user surface.
    private async Task<bool> AuthorizedAsync(CancellationToken ct) =>
        LocalTokenAuthenticator.IsAllowed(await authenticator.AuthenticateAsync(clientToken.Value, ct));

    private const string UnauthorizedCode = "unauthorized";
    private const string UnauthorizedMessage = "A valid local-access token is required.";

    public async Task<ListDatabasesResponse> ListDatabasesAsync(CancellationToken ct = default)
    {
        if (!await AuthorizedAsync(ct))
            return new ListDatabasesResponse(Ok: false, [], UnauthorizedCode, UnauthorizedMessage);

        var items = await connections.ListAsync(ct);
        return new ListDatabasesResponse(Ok: true, items
            .Select(c => new DatabaseSummary(c.Id.ToString(), c.Name, ProviderName(c.ProviderType), c.IsReadOnly))
            .ToList());
    }

    public async Task<DescribeSchemaResponse> DescribeSchemaAsync(string databaseId, CancellationToken ct = default)
    {
        if (!await AuthorizedAsync(ct))
            return SchemaError(UnauthorizedCode, UnauthorizedMessage);

        if (!Guid.TryParse(databaseId, out var id))
            return SchemaError("invalid_database_id", $"'{databaseId}' is not a valid database id.");

        // Existence is checked here (not left to the null SchemaService returns) so a missing connection
        // and a missing secret get distinct codes instead of collapsing into one.
        if (await connections.GetAsync(id, ct) is null)
            return SchemaError("connection_not_found", "No such database connection.");

        DatabaseSchema? schema;
        try
        {
            schema = await schemas.GetVisibleSchemaAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SchemaError("schema_extraction_error", ex.Message);
        }

        if (schema is null)
            return SchemaError("connection_secret_missing", "Connection secret is missing.");

        return new DescribeSchemaResponse(Ok: true, DatabaseId: id.ToString(),
            Tables: schema.Tables.Select(ToTable).ToList());
    }

    public async Task<QueryDatabaseResponse> QueryDatabaseAsync(string databaseId, string sql, CancellationToken ct = default)
    {
        if (!await AuthorizedAsync(ct))
            return new QueryDatabaseResponse(false, [], [], 0, false, 0, UnauthorizedCode, UnauthorizedMessage);

        if (!Guid.TryParse(databaseId, out var id))
            return new QueryDatabaseResponse(false, [], [], 0, false, 0,
                "invalid_database_id", $"'{databaseId}' is not a valid database id.");

        var r = await executor.ExecuteSqlAsync(id, sql, ct);
        return new QueryDatabaseResponse(
            r.Success, r.Columns, r.Rows, r.RowCount, r.Truncated, r.ElapsedMs, r.ErrorCode, r.ErrorMessage);
    }

    private static DescribeSchemaResponse SchemaError(string code, string message)
        => new(Ok: false, ErrorCode: code, ErrorMessage: message);

    private static TableDescription ToTable(SchemaTable t) => new(
        t.Schema, t.Name,
        t.Columns.Select(c => new ColumnDescription(c.Name, c.DataType, c.IsNullable)).ToList(),
        t.PrimaryKey,
        t.ForeignKeys.Select(f => new ForeignKeyDescription(
            f.Column, f.ReferencedSchema, f.ReferencedTable, f.ReferencedColumn)).ToList());

    private static string ProviderName(DatabaseProviderType type) => type switch
    {
        DatabaseProviderType.SqlServer => "sqlserver",
        DatabaseProviderType.Postgres => "postgres",
        _ => type.ToString().ToLowerInvariant(),
    };
}
