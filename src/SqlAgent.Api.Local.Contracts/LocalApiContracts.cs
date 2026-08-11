using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlAgent.Api.Local;

/// <summary>
/// Wire contract for the local named-pipe API (CD-50 T8, ADR-0003). These DTOs are the versionable
/// boundary between Core and the WPF client: they depend only on primitives, never on Core entities
/// or WPF view models, so either side can evolve without breaking the pipe. JSON is snake_case and
/// newline-delimited (one compact request object per line, one response object per line).
/// </summary>
public static class LocalApiContract
{
    /// <summary>Bumped only on a breaking wire change; echoed back in every response so clients can gate.</summary>
    public const int Version = 1;

    /// <summary>Default pipe name. The host may override, but this is the agreed default for the WPF client.</summary>
    public const string DefaultPipeName = "SqlAgent.LocalApi";

    /// <summary>Shared serializer options: snake_case, compact, enums as strings.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}

/// <summary>Stable, envelope-level error codes. Operation-specific codes (policy_denied_*, execution_*)
/// pass through verbatim from Core so the client sees one stable code set regardless of where it originated.</summary>
public static class ApiErrorCodes
{
    public const string UnknownOp = "unknown_op";
    public const string BadRequest = "bad_request";
    public const string DatabaseNotFound = "database_not_found";
    public const string ConnectionFailed = "connection_failed";
    public const string InternalError = "internal_error";
    /// <summary>A local-access token is required but the request presented none or an invalid one (CD-51 Story 1.7).</summary>
    public const string Unauthorized = "unauthorized";
}

/// <summary>A single request: an operation name, its raw params (decoded per-op by the dispatcher), and the
/// optional local-access <see cref="Token"/>. The token is only required when the host has one configured;
/// when it does not, it is ignored. Optional positional field, so existing callers compile unchanged.</summary>
public record LocalApiRequest(string Op, JsonElement? Params, string? Token = null);

/// <summary>A single response. Exactly one of <see cref="Data"/> / <see cref="Error"/> is set.</summary>
public record LocalApiResponse(int Version, bool Ok, JsonElement? Data, ApiError? Error)
{
    public static LocalApiResponse Success(JsonElement data) => new(LocalApiContract.Version, true, data, null);
    public static LocalApiResponse Fail(string code, string message) =>
        new(LocalApiContract.Version, false, null, new ApiError(code, message));
}

public record ApiError(string Code, string Message);

// ---- Operation params ----

public record GetDatabaseParams(Guid Id);
public record DeleteDatabaseParams(Guid Id);

/// <summary>Add (no <see cref="Id"/>) or update (with <see cref="Id"/>) a connection. The secret string is
/// write-only — it is accepted here but never returned by any read DTO.</summary>
public record SaveDatabaseParams(
    Guid? Id, string Name, DatabaseProviderTypeDto Provider, bool IsReadOnly, string? ConnectionString);

/// <summary>Test a saved connection (<see cref="Id"/>) or a draft (<see cref="Provider"/> + <see cref="ConnectionString"/>).</summary>
public record TestConnectionParams(Guid? Id, DatabaseProviderTypeDto? Provider, string? ConnectionString);

public record DescribeSchemaParams(Guid Id);

public record ExecuteSqlParams(Guid Id, string Sql);

/// <summary>Ask a database a natural-language question (CD-51 Story 1.4). The Core orchestration service
/// generates SQL, runs it through the same policy-validated path as execute_sql, and returns one outcome.</summary>
public record AskDatabaseParams(Guid Id, string Question);

public record ListTablePoliciesParams(Guid Id);

/// <summary>Set one table's visibility for a connection. A hidden table is excluded from the LLM schema and
/// blocked by query policy, so this is how the user scopes what the agent may touch (CD-50 visibility).</summary>
public record SetTablePolicyParams(Guid Id, string Schema, string Table, bool IsVisible);

// ---- Result DTOs ----

/// <summary>String mirror of Core's provider enum, so the wire stays readable and stable across enum edits.</summary>
public enum DatabaseProviderTypeDto { SqlServer, Postgres }

/// <summary>Read model for a configured database. Never carries the connection-string secret.</summary>
public record DatabaseDto(
    Guid Id, string Name, DatabaseProviderTypeDto Provider, bool IsReadOnly, bool HasSecret,
    DateTime CreatedAt, DateTime UpdatedAt);

public record ConnectionTestDto(bool Success, string? Error, string? ServerVersion, long ElapsedMs);

public record SchemaDto(IReadOnlyList<TableDto> Tables);
public record TableDto(
    string Schema, string Name, IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<string> PrimaryKey, IReadOnlyList<ForeignKeyDto> ForeignKeys);
public record ColumnDto(string Name, string DataType, bool IsNullable);
public record ForeignKeyDto(string Column, string ReferencedSchema, string ReferencedTable, string ReferencedColumn);

/// <summary>Query result. <see cref="Truncated"/> is how a too-large result surfaces: rows are capped, not errored.</summary>
public record QueryResultDto(
    string Sql, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowCount, bool Truncated, long ElapsedMs);

public record DeletedDto(bool Deleted);

/// <summary>Which of the three ask_database outcomes a result carries (mirrors Core's NlResponseKind).</summary>
public enum NlResponseKindDto { QueryResult, ClarificationRequired, Error }

/// <summary>The ask_database result: exactly one outcome (<see cref="Kind"/>). A query_result carries the
/// generated SQL plus the executed rows; clarification_required carries only <see cref="ClarificationQuestion"/>
/// (no SQL ran); error carries a stable <see cref="ErrorCode"/> and user-safe <see cref="ErrorMessage"/>, and
/// still echoes <see cref="GeneratedSql"/> when one existed so the user can audit what was rejected. All three
/// ride on a successful (ok=true) envelope — like test_connection, a rejection is a normal outcome, not a
/// transport error — so the error outcome can still carry the generated SQL.</summary>
public record AskDatabaseResultDto(
    NlResponseKindDto Kind,
    string? GeneratedSql,
    string? ClarificationQuestion,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowCount,
    bool Truncated,
    long ElapsedMs);

/// <summary>One table with its effective visibility. Returned for every live table; <see cref="IsVisible"/>
/// defaults to true when the connection has no policy row for the table.</summary>
public record TablePolicyDto(string Schema, string Table, bool IsVisible);

public record TablePoliciesDto(IReadOnlyList<TablePolicyDto> Tables);
