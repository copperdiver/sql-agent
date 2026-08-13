using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>
/// One turn, end to end, against the real NlQueryService over doubles. The invariant these exist to
/// protect is that the user's question reaches disk before anything can fail: a gateway that throws, a
/// dropped circuit, or a closed tab must never cost the typed text.
/// </summary>
public class ChatTurnServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly SqlAgentDbContext _db;
    private readonly ChatService _chats;
    private readonly TurnGatewayStub _gateway = new();
    private readonly TurnProviderStub _provider = new();
    private readonly DatabaseConnectionService _connections;
    private readonly ChatTurnService _turns;

    public ChatTurnServiceTests()
    {
        _conn.Open();
        _db = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();

        var registry = new DatabaseProviderRegistry([_provider]);
        _connections = new DatabaseConnectionService(_db, new InMemorySecretStore());
        _chats = new ChatService(_db);
        var executor = new QueryExecutionService(
            _connections, registry, _db, NullLogger<QueryExecutionService>.Instance);
        var schemas = new SchemaService(_connections, registry, _db);
        _turns = new ChatTurnService(
            _chats, new NlQueryService(_connections, schemas, executor, _gateway), _connections);
    }

    [Fact]
    public async Task Sending_with_no_chat_creates_one_titled_after_the_question()
    {
        var id = await NewConnectionAsync("prod");

        var turn = await _turns.SendAsync(null, "how many orders", [id]);

        Assert.Equal("how many orders", (await _chats.GetChatAsync(turn.ChatId))!.Title);
    }

    [Fact]
    public async Task Sending_with_no_database_attached_answers_with_a_code_and_calls_nothing()
    {
        var turn = await _turns.SendAsync(null, "how many orders", []);

        Assert.Equal(ChatOutcomeKind.Error, turn.AssistantMessage.OutcomeKind);
        Assert.Equal("no_database_attached", turn.AssistantMessage.ErrorCode);
        Assert.Equal(0, _gateway.CallCount);
        // The question is still on disk with its own message, so the transcript is not a lone answer.
        var detail = await _chats.GetChatAsync(turn.ChatId);
        Assert.Equal([ChatRole.User, ChatRole.Assistant], detail!.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task Sending_with_two_databases_attached_says_so_rather_than_picking_one()
    {
        // Silently querying the first attachment would answer about a database the user did not single
        // out, with nothing in the transcript admitting the choice was made for them.
        var a = await NewConnectionAsync("a");
        var b = await NewConnectionAsync("b");

        var turn = await _turns.SendAsync(null, "compare them", [a, b]);

        Assert.Equal("multiple_databases_unsupported", turn.AssistantMessage.ErrorCode);
        Assert.Equal(0, _gateway.CallCount);
        // Both are recorded against the question, so the transcript shows what was asked of what.
        var user = (await _chats.GetChatAsync(turn.ChatId))!.Messages.First();
        Assert.Equal(["a", "b"], user.Databases.Select(d => d.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task A_single_database_goes_through_the_ordinary_ask_path_and_persists_the_metadata()
    {
        var id = await NewConnectionAsync("prod");
        _gateway.NextResponse = LlmSqlResponse.Generated("SELECT id FROM orders");
        _provider.NextResult = new QueryResultSet(
            ["id"], [new object?[] { 1 }, new object?[] { 2 }], Truncated: true);

        var turn = await _turns.SendAsync(null, "orders", [id]);

        var answer = turn.AssistantMessage;
        Assert.Equal(ChatOutcomeKind.QueryResult, answer.OutcomeKind);
        Assert.Equal("SELECT id FROM orders", answer.GeneratedSql);
        Assert.Equal(2, answer.RowCount);
        Assert.True(answer.Truncated);
        // Rows come back on the live result for this render only.
        Assert.Equal(2, turn.Live!.Rows.Count);
    }

    [Fact]
    public async Task A_gateway_that_is_not_configured_still_leaves_a_question_and_an_answer_on_disk()
    {
        // Until the model service exists this is the ONLY path a real user takes, so it is the path that
        // proves persistence works at all. NotSupportedException is the documented "nothing is wired"
        // signal (see UnavailableLlmSqlGateway); NlQueryService maps it to llm_not_configured.
        var id = await NewConnectionAsync("prod");
        _gateway.Throw = new NotSupportedException("no provider");

        var turn = await _turns.SendAsync(null, "orders", [id]);

        Assert.Equal("llm_not_configured", turn.AssistantMessage.ErrorCode);
        var reloaded = await _chats.GetChatAsync(turn.ChatId);
        Assert.Equal(2, reloaded!.Messages.Count);
        Assert.Equal("orders", reloaded.Messages[0].Text);
        Assert.Equal("llm_not_configured", reloaded.Messages[1].ErrorCode);
    }

    [Fact]
    public async Task A_gateway_that_throws_anything_else_leaves_the_question_on_disk_too()
    {
        var id = await NewConnectionAsync("prod");
        _gateway.Throw = new HttpRequestException("connection reset");

        var turn = await _turns.SendAsync(null, "orders", [id]);

        Assert.Equal("llm_error", turn.AssistantMessage.ErrorCode);
        // The provider's own words never reach the transcript — they can echo a connection string.
        Assert.DoesNotContain("connection reset", turn.AssistantMessage.Text);
        Assert.Equal(2, (await _chats.GetChatAsync(turn.ChatId))!.Messages.Count);
    }

    [Fact]
    public async Task An_attached_database_that_was_deleted_is_recorded_by_name_with_no_id()
    {
        // Chips live in circuit state, and a connection can be deleted from another tab between
        // attaching and sending.
        var id = await NewConnectionAsync("prod");
        await _connections.DeleteAsync(id);

        var turn = await _turns.SendAsync(null, "orders", [id]);

        var user = (await _chats.GetChatAsync(turn.ChatId))!.Messages.First();
        var attached = Assert.Single(user.Databases);
        Assert.Null(attached.ConnectionId);
        Assert.Equal("(deleted database)", attached.Name);
        Assert.Equal("connection_not_found", turn.AssistantMessage.ErrorCode);
    }

    [Fact]
    public async Task An_empty_question_is_refused_before_anything_is_written()
    {
        // The composer disables Send for blank text, but Enter-to-send is a second entry point, so the
        // rule has to hold here too. Nothing is persisted: an empty user message would sit in the
        // transcript forever.
        var id = await NewConnectionAsync("prod");

        await Assert.ThrowsAsync<ArgumentException>(() => _turns.SendAsync(null, "   ", [id]));

        Assert.Empty(await _db.Chats.ToListAsync());
    }

    [Fact]
    public async Task A_cancellation_mid_query_still_leaves_a_question_and_an_answer_on_disk()
    {
        // QueryExecutionService deliberately turns a tripped token into a graceful execution_canceled
        // result instead of throwing, precisely so a stop click still yields a real answer. If that
        // answer were then persisted with the same spent token, the save itself would throw and undo
        // the layer below's work — the orphaned-question shape this whole service exists to prevent.
        var id = await NewConnectionAsync("prod");
        _gateway.NextResponse = LlmSqlResponse.Generated("SELECT id FROM orders");
        _provider.Block = true;

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var turn = await _turns.SendAsync(null, "orders", [id], cts.Token);

        Assert.Equal("execution_canceled", turn.AssistantMessage.ErrorCode);
        var reloaded = await _chats.GetChatAsync(turn.ChatId);
        Assert.Equal(2, reloaded!.Messages.Count);
        Assert.Equal(ChatRole.User, reloaded.Messages[0].Role);
        Assert.Equal(ChatRole.Assistant, reloaded.Messages[1].Role);
        Assert.Equal("execution_canceled", reloaded.Messages[1].ErrorCode);
    }

    [Fact]
    public async Task A_cancellation_while_the_model_is_still_generating_still_leaves_a_question_and_a_canceled_turn_on_disk()
    {
        // Unlike the query-execution path above, NlQueryService does NOT swallow a cancellation that
        // happens while the model is still being asked — it rethrows (see its own
        // `catch (OperationCanceledException) { throw; }`). Before this fix that exception passed
        // straight through ChatTurnService and out to the caller: the chat row and the question were
        // already on disk, but the caller never got a ChatId back, so it could not navigate to the
        // conversation, could not tell the sidebar, and the next send created a second chat. This is the
        // path stopping on the very first message of a new chat takes.
        var id = await NewConnectionAsync("prod");
        _gateway.Block = true;

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var turn = await _turns.SendAsync(null, "orders", [id], cts.Token);

        Assert.Equal("execution_canceled", turn.AssistantMessage.ErrorCode);
        Assert.Null(turn.Live);
        var reloaded = await _chats.GetChatAsync(turn.ChatId);
        Assert.Equal(2, reloaded!.Messages.Count);
        Assert.Equal(ChatRole.User, reloaded.Messages[0].Role);
        Assert.Equal("orders", reloaded.Messages[0].Text);
        Assert.Equal(ChatRole.Assistant, reloaded.Messages[1].Role);
        Assert.Equal("execution_canceled", reloaded.Messages[1].ErrorCode);
    }

    private async Task<Guid> NewConnectionAsync(string name) =>
        (await _connections.CreateAsync(
            new DatabaseConnectionInput(name, DatabaseProviderType.Postgres, IsReadOnly: true), "cs")).Id;

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}

/// <summary>Gateway double: a canned response, an exception thrown from the call, or a hang on the
/// supplied token (<see cref="Block"/>) so the cancel-while-generating path can be driven the same way
/// <see cref="TurnProviderStub.Block"/> drives cancel-while-executing.</summary>
sealed class TurnGatewayStub : ILlmSqlGateway
{
    public LlmSqlResponse? NextResponse { get; set; }
    public Exception? Throw { get; set; }
    public bool Block { get; set; }
    public int CallCount { get; private set; }

    public async Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default)
    {
        CallCount++;
        if (Block) await Task.Delay(Timeout.Infinite, ct);
        if (Throw is { } ex) throw ex;
        return NextResponse ?? LlmSqlResponse.Generated("SELECT 1");
    }
}

/// <summary>Provider double returning a canned result set over an empty schema, or hanging on the
/// supplied token until it is cancelled (<see cref="Block"/>) so QueryExecutionService's cancellation
/// path can be driven the same way QueryExecutionServiceTests drives it.</summary>
sealed class TurnProviderStub : IDatabaseProvider
{
    public QueryResultSet NextResult { get; set; } = new([], [], false);
    public bool Block { get; set; }
    public DatabaseProviderType ProviderType => DatabaseProviderType.Postgres;

    public Task<ConnectionTestResult> TestConnectionAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(ConnectionTestResult.Ok(null, 0));

    public Task<DatabaseSchema> GetSchemaAsync(string cs, CancellationToken ct = default)
        => Task.FromResult(new DatabaseSchema([]));

    public async Task<QueryResultSet> ExecuteQueryAsync(
        string cs, string sql, QueryExecutionOptions o, CancellationToken ct = default)
    {
        if (Block) await Task.Delay(Timeout.Infinite, ct);
        return NextResult;
    }
}
