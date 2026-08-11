using System.Text.Json;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Api.Local;

/// <summary>
/// Transport-agnostic request router for the local API (CD-50 T8). Takes one request envelope, calls the
/// matching Core/Storage service, and returns one response envelope — mapping Core types to wire DTOs and
/// Core/policy error codes to stable API codes. The named-pipe server is a thin loop over this class; all
/// contract behavior is here so it stays unit-testable without a live pipe or database server.
/// </summary>
public class LocalApiDispatcher(
    DatabaseConnectionService connections,
    ConnectionTester connectionTester,
    SchemaService schema,
    QueryExecutionService queries,
    TablePolicyService tablePolicies,
    NlQueryService nlQueries,
    LocalTokenAuthenticator authenticator)
{
    /// <summary>Parses, routes, and serializes one request. Never throws — failures become error responses.
    /// Every request is authenticated (CD-51 Story 1.7) before any operation runs; when no token is configured
    /// the gate passes through.</summary>
    public async Task<string> HandleAsync(string requestJson, CancellationToken ct = default)
    {
        LocalApiResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<LocalApiRequest>(requestJson, LocalApiContract.Json);
            if (request is null)
                response = LocalApiResponse.Fail(ApiErrorCodes.BadRequest, "Empty or malformed request.");
            else if (!LocalTokenAuthenticator.IsAllowed(await authenticator.AuthenticateAsync(request.Token, ct)))
                response = LocalApiResponse.Fail(ApiErrorCodes.Unauthorized, "A valid local-access token is required.");
            else
                response = await RouteAsync(request, ct);
        }
        catch (JsonException ex)
        {
            response = LocalApiResponse.Fail(ApiErrorCodes.BadRequest, $"Invalid JSON: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            throw; // shutdown / client gone — let the server loop handle it, don't audit as an op error.
        }
        catch (Exception ex)
        {
            response = LocalApiResponse.Fail(ApiErrorCodes.InternalError, ex.Message);
        }
        return JsonSerializer.Serialize(response, LocalApiContract.Json);
    }

    private Task<LocalApiResponse> RouteAsync(LocalApiRequest request, CancellationToken ct) => request.Op switch
    {
        "list_databases" => ListDatabasesAsync(ct),
        "get_database" => GetDatabaseAsync(Params<GetDatabaseParams>(request), ct),
        "save_database" => SaveDatabaseAsync(Params<SaveDatabaseParams>(request), ct),
        "delete_database" => DeleteDatabaseAsync(Params<DeleteDatabaseParams>(request), ct),
        "test_connection" => TestConnectionAsync(Params<TestConnectionParams>(request), ct),
        "describe_schema" => DescribeSchemaAsync(Params<DescribeSchemaParams>(request), ct),
        "refresh_schema" => RefreshSchemaAsync(Params<DescribeSchemaParams>(request), ct),
        "execute_sql" => ExecuteSqlAsync(Params<ExecuteSqlParams>(request), ct),
        "ask_database" => AskDatabaseAsync(Params<AskDatabaseParams>(request), ct),
        "list_table_policies" => ListTablePoliciesAsync(Params<ListTablePoliciesParams>(request), ct),
        "set_table_policy" => SetTablePolicyAsync(Params<SetTablePolicyParams>(request), ct),
        _ => Task.FromResult(LocalApiResponse.Fail(ApiErrorCodes.UnknownOp, $"Unknown operation '{request.Op}'.")),
    };

    private async Task<LocalApiResponse> ListDatabasesAsync(CancellationToken ct)
    {
        var list = await connections.ListAsync(ct);
        return Ok(list.Select(ToDto).ToList());
    }

    private async Task<LocalApiResponse> GetDatabaseAsync(GetDatabaseParams p, CancellationToken ct)
    {
        var info = await connections.GetAsync(p.Id, ct);
        return info is null ? NotFound(p.Id) : Ok(ToDto(info));
    }

    private async Task<LocalApiResponse> SaveDatabaseAsync(SaveDatabaseParams p, CancellationToken ct)
    {
        var input = new DatabaseConnectionInput(p.Name, ToCore(p.Provider), p.IsReadOnly);
        if (p.Id is null)
        {
            // Create requires the secret up front; without it there is nothing to test or execute against.
            if (string.IsNullOrEmpty(p.ConnectionString))
                return LocalApiResponse.Fail(ApiErrorCodes.BadRequest, "connection_string is required when creating a connection.");
            return Ok(ToDto(await connections.CreateAsync(input, p.ConnectionString, ct)));
        }
        // Update: a null/empty connection_string keeps the existing secret (rotate only when supplied).
        var updated = await connections.UpdateAsync(
            p.Id.Value, input, string.IsNullOrEmpty(p.ConnectionString) ? null : p.ConnectionString, ct);
        return updated is null ? NotFound(p.Id.Value) : Ok(ToDto(updated));
    }

    private async Task<LocalApiResponse> DeleteDatabaseAsync(DeleteDatabaseParams p, CancellationToken ct)
    {
        var deleted = await connections.DeleteAsync(p.Id, ct);
        return deleted ? Ok(new DeletedDto(true)) : NotFound(p.Id);
    }

    private async Task<LocalApiResponse> TestConnectionAsync(TestConnectionParams p, CancellationToken ct)
    {
        ConnectionTestResult? result;
        if (p.Id is not null)
        {
            result = await connectionTester.TestSavedAsync(p.Id.Value, ct);
            if (result is null) return NotFound(p.Id.Value); // missing connection or missing secret
        }
        else if (p.Provider is not null && !string.IsNullOrEmpty(p.ConnectionString))
        {
            result = await connectionTester.TestDraftAsync(ToCore(p.Provider.Value), p.ConnectionString, ct);
        }
        else
        {
            return LocalApiResponse.Fail(ApiErrorCodes.BadRequest, "Provide either id, or provider and connection_string.");
        }

        // A reachable-but-rejecting server is a normal ConnectionTestResult, not an envelope error — the
        // client reads success/error from the DTO. We surface connection_failed only as the DTO's content.
        return Ok(new ConnectionTestDto(result.Success, result.Error, result.ServerVersion, result.ElapsedMs));
    }

    private async Task<LocalApiResponse> DescribeSchemaAsync(DescribeSchemaParams p, CancellationToken ct)
    {
        var s = await schema.GetVisibleSchemaAsync(p.Id, ct);
        return s is null ? NotFound(p.Id) : Ok(ToDto(s));
    }

    // Manual refresh (CD-51 Story 1.5): re-extracts and re-caches the filtered schema, returning the new
    // description. Reuses DescribeSchemaParams (id only) and the SchemaDto response.
    private async Task<LocalApiResponse> RefreshSchemaAsync(DescribeSchemaParams p, CancellationToken ct)
    {
        var s = await schema.RefreshAsync(p.Id, ct);
        return s is null ? NotFound(p.Id) : Ok(ToDto(s));
    }

    private async Task<LocalApiResponse> ExecuteSqlAsync(ExecuteSqlParams p, CancellationToken ct)
    {
        var r = await queries.ExecuteSqlAsync(p.Id, p.Sql, ct);
        if (r.Success)
            return Ok(new QueryResultDto(r.Sql, r.Columns, r.Rows, r.RowCount, r.Truncated, r.ElapsedMs));

        // Core already emits stable codes (policy_denied_*, execution_timeout, execution_error, ...).
        // Re-map only the "no config row" code to the API's database_not_found; pass the rest through verbatim.
        var code = r.ErrorCode == "connection_not_found" ? ApiErrorCodes.DatabaseNotFound : r.ErrorCode!;
        return LocalApiResponse.Fail(code, r.ErrorMessage ?? "Query failed.");
    }

    // ask_database delegates the whole flow to Core's NlQueryService (no policy logic duplicated here) and
    // maps its one-outcome result to the wire DTO. All three outcomes — query_result, clarification_required,
    // error — ride on a successful envelope: like test_connection, a clarification or policy rejection is a
    // normal answer, not a transport error, and ok=false would drop the generated_sql the error outcome echoes.
    private async Task<LocalApiResponse> AskDatabaseAsync(AskDatabaseParams p, CancellationToken ct)
    {
        var r = await nlQueries.AskAsync(p.Id, p.Question, ct);
        return Ok(ToDto(r));
    }

    private async Task<LocalApiResponse> ListTablePoliciesAsync(ListTablePoliciesParams p, CancellationToken ct)
    {
        var tables = await tablePolicies.ListAsync(p.Id, ct);
        return tables is null
            ? NotFound(p.Id)
            : Ok(new TablePoliciesDto(tables.Select(t => new TablePolicyDto(t.Schema, t.Table, t.IsVisible)).ToList()));
    }

    private async Task<LocalApiResponse> SetTablePolicyAsync(SetTablePolicyParams p, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(p.Table))
            return LocalApiResponse.Fail(ApiErrorCodes.BadRequest, "table is required.");
        var ok = await tablePolicies.SetVisibilityAsync(p.Id, p.Schema ?? "", p.Table, p.IsVisible, ct);
        return ok ? Ok(new TablePolicyDto(p.Schema ?? "", p.Table, p.IsVisible)) : NotFound(p.Id);
    }

    // ---- helpers ----

    /// <summary>Decodes the op's params block, defaulting an omitted block to an empty object so required-field
    /// validation produces a clean bad_request rather than a null-ref.</summary>
    private static T Params<T>(LocalApiRequest request)
    {
        var json = request.Params ?? JsonSerializer.SerializeToElement(new { }, LocalApiContract.Json);
        return json.Deserialize<T>(LocalApiContract.Json)
            ?? throw new JsonException($"Missing params for '{request.Op}'.");
    }

    private static LocalApiResponse Ok<T>(T data) =>
        LocalApiResponse.Success(JsonSerializer.SerializeToElement(data, LocalApiContract.Json));

    private static LocalApiResponse NotFound(Guid id) =>
        LocalApiResponse.Fail(ApiErrorCodes.DatabaseNotFound, $"No database connection '{id}'.");

    private static DatabaseDto ToDto(DatabaseConnectionInfo i) =>
        new(i.Id, i.Name, ToDto(i.ProviderType), i.IsReadOnly, i.HasSecret, i.CreatedAt, i.UpdatedAt);

    private static SchemaDto ToDto(DatabaseSchema s) => new(s.Tables.Select(t => new TableDto(
        t.Schema, t.Name,
        t.Columns.Select(c => new ColumnDto(c.Name, c.DataType, c.IsNullable)).ToList(),
        t.PrimaryKey,
        t.ForeignKeys.Select(f => new ForeignKeyDto(f.Column, f.ReferencedSchema, f.ReferencedTable, f.ReferencedColumn)).ToList()))
        .ToList());

    private static AskDatabaseResultDto ToDto(NlQueryResult r) => new(
        ToDto(r.Kind), r.GeneratedSql, r.ClarificationQuestion, r.ErrorCode, r.ErrorMessage,
        r.Columns, r.Rows, r.RowCount, r.Truncated, r.ElapsedMs);

    private static NlResponseKindDto ToDto(NlResponseKind k) => k switch
    {
        NlResponseKind.QueryResult => NlResponseKindDto.QueryResult,
        NlResponseKind.ClarificationRequired => NlResponseKindDto.ClarificationRequired,
        NlResponseKind.Error => NlResponseKindDto.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(k), k, "Unmapped NL response kind."),
    };

    private static DatabaseProviderTypeDto ToDto(DatabaseProviderType t) => t switch
    {
        DatabaseProviderType.SqlServer => DatabaseProviderTypeDto.SqlServer,
        DatabaseProviderType.Postgres => DatabaseProviderTypeDto.Postgres,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Unmapped provider type."),
    };

    private static DatabaseProviderType ToCore(DatabaseProviderTypeDto t) => t switch
    {
        DatabaseProviderTypeDto.SqlServer => DatabaseProviderType.SqlServer,
        DatabaseProviderTypeDto.Postgres => DatabaseProviderType.Postgres,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Unmapped provider type."),
    };
}
