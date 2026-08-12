using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>LLM seam double: returns a canned response (or throws) and records the request it was handed.</summary>
file sealed class FakeGateway(LlmSqlResponse? response = null, Exception? throws = null) : ILlmSqlGateway
{
    public LlmSqlRequest? LastRequest { get; private set; }

    public Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        if (throws is not null) throw throws;
        return Task.FromResult(response ?? LlmSqlResponse.Generated("SELECT 1"));
    }
}

/// <summary>Provider double that returns a canned schema/result and records whether SQL was executed.</summary>
file sealed class NlFakeProvider(DatabaseSchema? schema = null, QueryResultSet? result = null) : IDatabaseProvider
{
    public bool Executed { get; private set; }
    public DatabaseProviderType ProviderType => DatabaseProviderType.Postgres;

    public Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string connectionString, CancellationToken ct = default)
        => Task.FromResult(schema ?? new DatabaseSchema([]));

    public Task<QueryResultSet> ExecuteQueryAsync(
        string connectionString, string sql, QueryExecutionOptions options, CancellationToken ct = default)
    {
        Executed = true;
        return Task.FromResult(result ?? new QueryResultSet(["n"], [new object?[] { 1 }], false));
    }
}

public class NlQueryServiceTests
{
    private static readonly DatabaseSchema Schema = new([
        new SchemaTable("public", "orders",
            [new SchemaColumn("id", "int", false), new SchemaColumn("total", "numeric", true)], ["id"], [], []),
        new SchemaTable("public", "secrets",
            [new SchemaColumn("token", "text", false)], [], [], []),
    ]);

    private static (SqlAgentDbContext db, SqliteConnection conn) NewStore()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new SqlAgentDbContext(new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static (NlQueryService svc, DatabaseConnectionService connections) Build(
        SqlAgentDbContext db, IDatabaseProvider provider, ILlmSqlGateway gateway)
    {
        var connections = new DatabaseConnectionService(db, new InMemorySecretStore());
        var registry = new DatabaseProviderRegistry([provider]);
        var svc = new NlQueryService(connections, new SchemaService(connections, registry, db),
            new QueryExecutionService(connections, registry, db), gateway);
        return (svc, connections);
    }

    private static async Task<Guid> AddConnectionAsync(DatabaseConnectionService connections, bool readOnly = true)
    {
        var c = await connections.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, readOnly), "cs");
        return c.Id;
    }

    private static void HideSecrets(SqlAgentDbContext db, Guid connectionId)
    {
        db.TablePolicies.Add(new TablePolicy
        {
            Id = Guid.NewGuid(),
            DatabaseConnectionId = connectionId,
            SchemaName = "public",
            TableName = "secrets",
            IsVisible = false,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Success_returns_query_result_with_generated_sql()
    {
        var (db, conn) = NewStore();
        var gateway = new FakeGateway(LlmSqlResponse.Generated("SELECT id, total FROM orders"));
        var (svc, connections) = Build(db, new NlFakeProvider(Schema, new QueryResultSet(["id", "total"], [new object?[] { 1, 9 }], false)), gateway);
        var id = await AddConnectionAsync(connections);

        var r = await svc.AskAsync(id, "show me the orders");

        Assert.Equal(NlResponseKind.QueryResult, r.Kind);
        Assert.Equal("SELECT id, total FROM orders", r.GeneratedSql);
        Assert.Equal(["id", "total"], r.Columns);
        Assert.Equal(1, r.RowCount);
        conn.Dispose();
    }

    [Fact]
    public async Task Ambiguous_question_returns_clarification_without_executing()
    {
        var (db, conn) = NewStore();
        var provider = new NlFakeProvider(Schema);
        var (svc, connections) = Build(db, provider, new FakeGateway(LlmSqlResponse.Clarify("Which time range?")));
        var id = await AddConnectionAsync(connections);

        var r = await svc.AskAsync(id, "how are things");

        Assert.Equal(NlResponseKind.ClarificationRequired, r.Kind);
        Assert.Equal("Which time range?", r.ClarificationQuestion);
        Assert.Null(r.GeneratedSql);
        Assert.False(provider.Executed);                       // no SQL ran
        Assert.Empty(db.QueryAuditLogs);                       // and nothing was audited
        conn.Dispose();
    }

    [Fact]
    public async Task Generated_sql_hitting_hidden_table_is_rejected_by_policy()
    {
        var (db, conn) = NewStore();
        var provider = new NlFakeProvider(Schema);
        var (svc, connections) = Build(db, provider, new FakeGateway(LlmSqlResponse.Generated("SELECT token FROM secrets")));
        var id = await AddConnectionAsync(connections);
        HideSecrets(db, id);

        var r = await svc.AskAsync(id, "show the tokens");

        Assert.Equal(NlResponseKind.Error, r.Kind);
        Assert.Equal("policy_denied_hidden_table", r.ErrorCode);
        Assert.Equal("SELECT token FROM secrets", r.GeneratedSql);  // echoed so the user can audit it
        Assert.False(provider.Executed);
        conn.Dispose();
    }

    [Fact]
    public async Task Generated_write_on_readonly_is_rejected_by_policy()
    {
        var (db, conn) = NewStore();
        var (svc, connections) = Build(db, new NlFakeProvider(Schema), new FakeGateway(LlmSqlResponse.Generated("UPDATE orders SET total = 0")));
        var id = await AddConnectionAsync(connections, readOnly: true);

        var r = await svc.AskAsync(id, "zero out the orders");

        Assert.Equal(NlResponseKind.Error, r.Kind);
        Assert.Equal("policy_denied_readonly", r.ErrorCode);
        conn.Dispose();
    }

    [Fact]
    public async Task Gateway_failure_returns_stable_llm_error_without_leaking_exception()
    {
        var (db, conn) = NewStore();
        var (svc, connections) = Build(db, new NlFakeProvider(Schema), new FakeGateway(throws: new InvalidOperationException("boom: secret detail")));
        var id = await AddConnectionAsync(connections);

        var r = await svc.AskAsync(id, "anything");

        Assert.Equal(NlResponseKind.Error, r.Kind);
        Assert.Equal("llm_error", r.ErrorCode);
        Assert.DoesNotContain("boom", r.ErrorMessage);
        conn.Dispose();
    }

    [Fact]
    public async Task No_provider_configured_returns_a_distinct_code_from_a_genuine_gateway_failure()
    {
        // UnavailableLlmSqlGateway (the placeholder wired in Program.cs until a real provider lands)
        // signals "not configured" by throwing NotSupportedException specifically. That must map to its
        // own code rather than the generic llm_error used above for every other exception type — once a
        // real provider is wired, its own timeouts/network errors/malformed responses must still surface
        // as llm_error, not be mistaken for "no provider configured".
        var (db, conn) = NewStore();
        var (svc, connections) = Build(db, new NlFakeProvider(Schema),
            new FakeGateway(throws: new NotSupportedException("No LLM provider is configured on this server.")));
        var id = await AddConnectionAsync(connections);

        var r = await svc.AskAsync(id, "anything");

        Assert.Equal(NlResponseKind.Error, r.Kind);
        Assert.Equal("llm_not_configured", r.ErrorCode);
        conn.Dispose();
    }

    [Fact]
    public async Task Prompt_context_excludes_hidden_tables()
    {
        var (db, conn) = NewStore();
        var gateway = new FakeGateway(LlmSqlResponse.Clarify("?"));
        var (svc, connections) = Build(db, new NlFakeProvider(Schema), gateway);
        var id = await AddConnectionAsync(connections);
        HideSecrets(db, id);

        await svc.AskAsync(id, "anything");

        Assert.NotNull(gateway.LastRequest);
        Assert.Contains("orders", gateway.LastRequest!.SchemaContext);
        Assert.DoesNotContain("secrets", gateway.LastRequest.SchemaContext);
        conn.Dispose();
    }

    [Fact]
    public async Task Prompt_context_includes_dialect_hints()
    {
        var (db, conn) = NewStore();
        var gateway = new FakeGateway(LlmSqlResponse.Clarify("?"));
        var (svc, connections) = Build(db, new NlFakeProvider(Schema), gateway);
        var id = await AddConnectionAsync(connections); // Postgres connection (see Build/AddConnectionAsync)

        await svc.AskAsync(id, "anything");

        // The schema text must be preceded by the target-dialect guidance so the model emits portable SQL.
        Assert.Contains("LIMIT", gateway.LastRequest!.SchemaContext);
        Assert.Contains("RETURNING", gateway.LastRequest.SchemaContext);
        conn.Dispose();
    }

    [Fact]
    public async Task Prompt_context_includes_column_sizing()
    {
        var (db, conn) = NewStore();
        var sized = new DatabaseSchema([
            new SchemaTable("public", "orders",
                [
                    new SchemaColumn("code", "varchar", false, MaxLength: 20),
                    new SchemaColumn("total", "numeric", true, Precision: 10, Scale: 2),
                ],
                ["code"], [], []),
        ]);
        var gateway = new FakeGateway(LlmSqlResponse.Clarify("?"));
        var (svc, connections) = Build(db, new NlFakeProvider(sized), gateway);
        var id = await AddConnectionAsync(connections);

        await svc.AskAsync(id, "anything");

        // Without declared sizes the model cannot tell varchar(20) from varchar(max) when writing predicates.
        Assert.Contains("varchar(20)", gateway.LastRequest!.SchemaContext);
        Assert.Contains("numeric(10,2)", gateway.LastRequest.SchemaContext);
        conn.Dispose();
    }

    [Fact]
    public async Task Empty_question_short_circuits()
    {
        var (db, conn) = NewStore();
        var (svc, connections) = Build(db, new NlFakeProvider(Schema), new FakeGateway());
        var id = await AddConnectionAsync(connections);

        var r = await svc.AskAsync(id, "   ");

        Assert.Equal(NlResponseKind.Error, r.Kind);
        Assert.Equal("question_empty", r.ErrorCode);
        conn.Dispose();
    }

    [Fact]
    public async Task Unknown_connection_returns_connection_not_found()
    {
        var (db, conn) = NewStore();
        var (svc, _) = Build(db, new NlFakeProvider(Schema), new FakeGateway());

        var r = await svc.AskAsync(Guid.NewGuid(), "anything");

        Assert.Equal(NlResponseKind.Error, r.Kind);
        Assert.Equal("connection_not_found", r.ErrorCode);
        conn.Dispose();
    }
}
