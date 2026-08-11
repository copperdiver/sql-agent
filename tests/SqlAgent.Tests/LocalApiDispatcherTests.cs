using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Api.Local;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// Provider double for the local-API contract tests: canned schema + connection test, and a query result
/// gated only by policy (the dispatcher/executor decide allow/deny). No live server required.
/// </summary>
file sealed class ApiFakeProvider(
    DatabaseProviderType type,
    DatabaseSchema? schema = null,
    QueryResultSet? result = null,
    ConnectionTestResult? test = null) : IDatabaseProvider
{
    public DatabaseProviderType ProviderType => type;

    public Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default)
        => Task.FromResult(test ?? ConnectionTestResult.Ok("v1.0", 5));

    public Task<DatabaseSchema> GetSchemaAsync(string connectionString, CancellationToken ct = default)
        => Task.FromResult(schema ?? new DatabaseSchema([]));

    public Task<QueryResultSet> ExecuteQueryAsync(
        string connectionString, string sql, QueryExecutionOptions options, CancellationToken ct = default)
        => Task.FromResult(result ?? new QueryResultSet(["id"], [new object?[] { 1 }], false));
}

/// <summary>LLM seam double for the ask_database contract tests: returns a canned response (or throws).</summary>
file sealed class ApiFakeGateway(LlmSqlResponse? response = null, Exception? throws = null) : ILlmSqlGateway
{
    public Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default)
    {
        if (throws is not null) throw throws;
        return Task.FromResult(response ?? LlmSqlResponse.Generated("SELECT 1"));
    }
}

public class LocalApiDispatcherTests
{
    private static (LocalApiDispatcher dispatcher, DatabaseConnectionService connections, SqlAgentDbContext db, SqliteConnection conn)
        NewDispatcher(IDatabaseProvider provider, ILlmSqlGateway? gateway = null, ISecretStore? secrets = null)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new SqlAgentDbContext(new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();

        secrets ??= new InMemorySecretStore();
        var connections = new DatabaseConnectionService(db, secrets);
        var registry = new DatabaseProviderRegistry([provider]);
        var schema = new SchemaService(connections, registry, db);
        var queries = new QueryExecutionService(connections, registry, db);
        var dispatcher = new LocalApiDispatcher(
            connections,
            new ConnectionTester(connections, registry),
            schema,
            queries,
            new TablePolicyService(connections, registry, db),
            new NlQueryService(connections, schema, queries, gateway ?? new ApiFakeGateway()),
            new LocalTokenAuthenticator(secrets, NullLogger<LocalTokenAuthenticator>.Instance));
        return (dispatcher, connections, db, conn);
    }

    /// <summary>Builds a request line and returns the parsed response envelope.</summary>
    private static async Task<LocalApiResponse> CallAsync(LocalApiDispatcher d, string op, object? @params = null)
    {
        var request = JsonSerializer.Serialize(
            new { op, @params }, LocalApiContract.Json);
        var json = await d.HandleAsync(request);
        return JsonSerializer.Deserialize<LocalApiResponse>(json, LocalApiContract.Json)!;
    }

    private static T Data<T>(LocalApiResponse r) => r.Data!.Value.Deserialize<T>(LocalApiContract.Json)!;

    [Fact]
    public async Task List_is_empty_then_save_returns_dto_without_leaking_secret()
    {
        var (d, _, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres));

        var empty = await CallAsync(d, "list_databases");
        Assert.True(empty.Ok);
        Assert.Equal(LocalApiContract.Version, empty.Version);
        Assert.Empty(Data<List<DatabaseDto>>(empty));

        var saved = await CallAsync(d, "save_database", new
        {
            name = "prod",
            provider = "postgres",
            is_read_only = true,
            connection_string = "Host=secret;Password=p@ss",
        });
        Assert.True(saved.Ok);
        var dto = Data<DatabaseDto>(saved);
        Assert.Equal("prod", dto.Name);
        Assert.True(dto.HasSecret);
        Assert.DoesNotContain("p@ss", saved.Data!.Value.GetRawText()); // secret never on the wire

        conn.Dispose();
    }

    [Fact]
    public async Task Get_missing_connection_returns_database_not_found()
    {
        var (d, _, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres));

        var r = await CallAsync(d, "get_database", new { id = Guid.NewGuid() });

        Assert.False(r.Ok);
        Assert.Equal(ApiErrorCodes.DatabaseNotFound, r.Error!.Code);

        conn.Dispose();
    }

    [Fact]
    public async Task Test_connection_draft_returns_result_dto()
    {
        var (d, _, _, conn) = NewDispatcher(
            new ApiFakeProvider(DatabaseProviderType.Postgres, test: ConnectionTestResult.Ok("PG 16", 12)));

        var r = await CallAsync(d, "test_connection", new
        {
            provider = "postgres",
            connection_string = "Host=localhost",
        });

        Assert.True(r.Ok);
        var dto = Data<ConnectionTestDto>(r);
        Assert.True(dto.Success);
        Assert.Equal("PG 16", dto.ServerVersion);

        conn.Dispose();
    }

    [Fact]
    public async Task Describe_schema_filters_hidden_tables()
    {
        var schema = new DatabaseSchema([
            new SchemaTable("public", "orders", [new SchemaColumn("id", "int", false)], ["id"], [], []),
            new SchemaTable("public", "secrets", [new SchemaColumn("id", "int", false)], ["id"], [], []),
        ]);
        var (d, connections, db, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres, schema: schema));
        var created = await connections.CreateAsync(new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, true), "cs");
        db.TablePolicies.Add(new TablePolicy
        {
            Id = Guid.NewGuid(), DatabaseConnectionId = created.Id,
            SchemaName = "public", TableName = "secrets", IsVisible = false,
        });
        await db.SaveChangesAsync();

        var r = await CallAsync(d, "describe_schema", new { id = created.Id });

        Assert.True(r.Ok);
        var dto = Data<SchemaDto>(r);
        Assert.Equal("orders", Assert.Single(dto.Tables).Name); // hidden table dropped

        conn.Dispose();
    }

    [Fact]
    public async Task Refresh_schema_caches_and_returns_the_filtered_description()
    {
        var schema = new DatabaseSchema([
            new SchemaTable("public", "orders", [new SchemaColumn("id", "int", false)], ["id"], [], []),
            new SchemaTable("public", "secrets", [new SchemaColumn("id", "int", false)], ["id"], [], []),
        ]);
        var (d, connections, db, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres, schema: schema));
        var created = await connections.CreateAsync(new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, true), "cs");
        db.TablePolicies.Add(new TablePolicy
        {
            Id = Guid.NewGuid(), DatabaseConnectionId = created.Id,
            SchemaName = "public", TableName = "secrets", IsVisible = false,
        });
        await db.SaveChangesAsync();

        var r = await CallAsync(d, "refresh_schema", new { id = created.Id });

        Assert.True(r.Ok);
        Assert.Equal("orders", Assert.Single(Data<SchemaDto>(r).Tables).Name);
        Assert.True(await db.SchemaCaches.AnyAsync(c => c.DatabaseConnectionId == created.Id)); // cached

        conn.Dispose();
    }

    [Fact]
    public async Task Refresh_schema_unknown_connection_returns_database_not_found()
    {
        var (d, _, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres));

        var r = await CallAsync(d, "refresh_schema", new { id = Guid.NewGuid() });

        Assert.False(r.Ok);
        Assert.Equal(ApiErrorCodes.DatabaseNotFound, r.Error!.Code);

        conn.Dispose();
    }

    [Fact]
    public async Task Execute_sql_success_returns_rows_and_metadata()
    {
        var result = new QueryResultSet(["id", "name"], [new object?[] { 1, "a" }], Truncated: true);
        var (d, connections, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres, result: result));
        var created = await connections.CreateAsync(new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, true), "cs");

        var r = await CallAsync(d, "execute_sql", new { id = created.Id, sql = "SELECT id, name FROM orders" });

        Assert.True(r.Ok);
        var dto = Data<QueryResultDto>(r);
        Assert.Equal(["id", "name"], dto.Columns);
        Assert.Equal(1, dto.RowCount);
        Assert.True(dto.Truncated); // size cap surfaces as truncation, not an error

        conn.Dispose();
    }

    [Fact]
    public async Task Execute_sql_write_on_readonly_passes_through_policy_code()
    {
        var (d, connections, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres));
        var created = await connections.CreateAsync(new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, true), "cs");

        var r = await CallAsync(d, "execute_sql", new { id = created.Id, sql = "UPDATE orders SET total = 0" });

        Assert.False(r.Ok);
        Assert.Equal("policy_denied_readonly", r.Error!.Code); // Core's stable code, surfaced verbatim

        conn.Dispose();
    }

    [Fact]
    public async Task Execute_sql_on_unknown_connection_maps_to_database_not_found()
    {
        var (d, _, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres));

        var r = await CallAsync(d, "execute_sql", new { id = Guid.NewGuid(), sql = "SELECT 1" });

        Assert.False(r.Ok);
        Assert.Equal(ApiErrorCodes.DatabaseNotFound, r.Error!.Code);

        conn.Dispose();
    }

    [Fact]
    public async Task Ask_database_returns_query_result_with_generated_sql()
    {
        var schema = new DatabaseSchema([
            new SchemaTable("public", "orders", [new SchemaColumn("id", "int", false)], ["id"], [], []),
        ]);
        var result = new QueryResultSet(["id"], [new object?[] { 7 }], false);
        var (d, connections, _, conn) = NewDispatcher(
            new ApiFakeProvider(DatabaseProviderType.Postgres, schema: schema, result: result),
            new ApiFakeGateway(LlmSqlResponse.Generated("SELECT id FROM orders")));
        var created = await connections.CreateAsync(new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, true), "cs");

        var r = await CallAsync(d, "ask_database", new { id = created.Id, question = "show the orders" });

        Assert.True(r.Ok); // a normal answer rides on a successful envelope
        var dto = Data<AskDatabaseResultDto>(r);
        Assert.Equal(NlResponseKindDto.QueryResult, dto.Kind);
        Assert.Equal("SELECT id FROM orders", dto.GeneratedSql); // echoed so the user can audit it
        Assert.Equal(["id"], dto.Columns);
        Assert.Equal(1, dto.RowCount);

        conn.Dispose();
    }

    [Fact]
    public async Task Ask_database_ambiguous_question_returns_clarification()
    {
        var schema = new DatabaseSchema([
            new SchemaTable("public", "orders", [new SchemaColumn("id", "int", false)], ["id"], [], []),
        ]);
        var (d, connections, _, conn) = NewDispatcher(
            new ApiFakeProvider(DatabaseProviderType.Postgres, schema: schema),
            new ApiFakeGateway(LlmSqlResponse.Clarify("Which time range?")));
        var created = await connections.CreateAsync(new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, true), "cs");

        var r = await CallAsync(d, "ask_database", new { id = created.Id, question = "how are things" });

        Assert.True(r.Ok);
        var dto = Data<AskDatabaseResultDto>(r);
        Assert.Equal(NlResponseKindDto.ClarificationRequired, dto.Kind);
        Assert.Equal("Which time range?", dto.ClarificationQuestion);
        Assert.Null(dto.GeneratedSql); // no SQL ran

        conn.Dispose();
    }

    [Fact]
    public async Task Ask_database_generated_write_on_readonly_returns_error_echoing_sql()
    {
        var schema = new DatabaseSchema([
            new SchemaTable("public", "orders", [new SchemaColumn("id", "int", false)], ["id"], [], []),
        ]);
        var (d, connections, _, conn) = NewDispatcher(
            new ApiFakeProvider(DatabaseProviderType.Postgres, schema: schema),
            new ApiFakeGateway(LlmSqlResponse.Generated("UPDATE orders SET id = 0")));
        var created = await connections.CreateAsync(new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, true), "cs");

        var r = await CallAsync(d, "ask_database", new { id = created.Id, question = "wipe the orders" });

        Assert.True(r.Ok); // the error outcome is data, not a transport failure — so it can carry the SQL
        var dto = Data<AskDatabaseResultDto>(r);
        Assert.Equal(NlResponseKindDto.Error, dto.Kind);
        Assert.Equal("policy_denied_readonly", dto.ErrorCode); // Core's stable code, surfaced verbatim
        Assert.Equal("UPDATE orders SET id = 0", dto.GeneratedSql); // echoed so the user can audit what was rejected

        conn.Dispose();
    }

    [Fact]
    public async Task Unknown_op_returns_unknown_op()
    {
        var (d, _, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres));

        var r = await CallAsync(d, "frobnicate");

        Assert.False(r.Ok);
        Assert.Equal(ApiErrorCodes.UnknownOp, r.Error!.Code);

        conn.Dispose();
    }

    [Fact]
    public async Task Malformed_json_returns_bad_request()
    {
        var (d, _, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres));

        var json = await d.HandleAsync("{ this is not json");
        var r = JsonSerializer.Deserialize<LocalApiResponse>(json, LocalApiContract.Json)!;

        Assert.False(r.Ok);
        Assert.Equal(ApiErrorCodes.BadRequest, r.Error!.Code);

        conn.Dispose();
    }

    [Fact]
    public async Task List_table_policies_reports_every_table_and_set_hides_one()
    {
        var schema = new DatabaseSchema([
            new SchemaTable("public", "orders", [new SchemaColumn("id", "int", false)], ["id"], [], []),
            new SchemaTable("public", "secrets", [new SchemaColumn("id", "int", false)], ["id"], [], []),
        ]);
        var (d, connections, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres, schema: schema));
        var created = await connections.CreateAsync(new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, true), "cs");

        // Every live table is listed, all visible by default (unlike describe_schema, which hides them).
        var listed = await CallAsync(d, "list_table_policies", new { id = created.Id });
        Assert.True(listed.Ok);
        var before = Data<TablePoliciesDto>(listed);
        Assert.Equal(2, before.Tables.Count);
        Assert.All(before.Tables, t => Assert.True(t.IsVisible));

        // Hiding one persists and is reflected on the next list.
        var set = await CallAsync(d, "set_table_policy",
            new { id = created.Id, schema = "public", table = "secrets", is_visible = false });
        Assert.True(set.Ok);

        var after = Data<TablePoliciesDto>(await CallAsync(d, "list_table_policies", new { id = created.Id }));
        Assert.False(after.Tables.Single(t => t.Table == "secrets").IsVisible);
        Assert.True(after.Tables.Single(t => t.Table == "orders").IsVisible);

        conn.Dispose();
    }

    [Fact]
    public async Task Set_table_policy_on_unknown_connection_returns_database_not_found()
    {
        var (d, _, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres));

        var r = await CallAsync(d, "set_table_policy",
            new { id = Guid.NewGuid(), schema = "public", table = "orders", is_visible = false });

        Assert.False(r.Ok);
        Assert.Equal(ApiErrorCodes.DatabaseNotFound, r.Error!.Code);

        conn.Dispose();
    }

    [Fact]
    public async Task Save_create_without_connection_string_is_bad_request()
    {
        var (d, _, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres));

        var r = await CallAsync(d, "save_database", new { name = "x", provider = "postgres", is_read_only = true });

        Assert.False(r.Ok);
        Assert.Equal(ApiErrorCodes.BadRequest, r.Error!.Code);

        conn.Dispose();
    }

    // ---- CD-51 Story 1.7: local-access token enforcement ----

    /// <summary>Sends a request carrying an explicit token (the disabled-token tests above omit it).</summary>
    private static async Task<LocalApiResponse> CallWithTokenAsync(LocalApiDispatcher d, string op, string? token)
    {
        var request = JsonSerializer.Serialize(new { op, token }, LocalApiContract.Json);
        var json = await d.HandleAsync(request);
        return JsonSerializer.Deserialize<LocalApiResponse>(json, LocalApiContract.Json)!;
    }

    [Fact]
    public async Task Unconfigured_token_allows_requests()
    {
        // No token seeded => auth disabled => the op runs even though the request presents none.
        var (d, _, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres));

        var r = await CallAsync(d, "list_databases");

        Assert.True(r.Ok);
        conn.Dispose();
    }

    [Fact]
    public async Task Configured_token_with_valid_token_allows_requests()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(LocalTokenAuthenticator.TokenSecretReference, "s3cret");
        var (d, _, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres), secrets: secrets);

        var r = await CallWithTokenAsync(d, "list_databases", "s3cret");

        Assert.True(r.Ok);
        conn.Dispose();
    }

    [Fact]
    public async Task Configured_token_with_missing_token_is_unauthorized()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(LocalTokenAuthenticator.TokenSecretReference, "s3cret");
        var (d, _, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres), secrets: secrets);

        var r = await CallWithTokenAsync(d, "list_databases", null);

        Assert.False(r.Ok);
        Assert.Equal(ApiErrorCodes.Unauthorized, r.Error!.Code);
        conn.Dispose();
    }

    [Fact]
    public async Task Configured_token_with_invalid_token_is_unauthorized()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetAsync(LocalTokenAuthenticator.TokenSecretReference, "s3cret");
        var (d, _, _, conn) = NewDispatcher(new ApiFakeProvider(DatabaseProviderType.Postgres), secrets: secrets);

        var r = await CallWithTokenAsync(d, "list_databases", "wrong");

        Assert.False(r.Ok);
        Assert.Equal(ApiErrorCodes.Unauthorized, r.Error!.Code);
        conn.Dispose();
    }
}
