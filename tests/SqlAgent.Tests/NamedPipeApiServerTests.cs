using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SqlAgent.Api.Local;
using SqlAgent.Core;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

/// <summary>Unused LLM seam: the pipe test only exercises list_databases, so ask_database never calls this.</summary>
file sealed class UnusedGateway : ILlmSqlGateway
{
    public Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default) =>
        Task.FromResult(LlmSqlResponse.Generated("SELECT 1"));
}

/// <summary>
/// Transport-level check: the named-pipe server frames newline-delimited JSON and round-trips a real
/// request to a real <see cref="LocalApiDispatcher"/>. Windows-only (named pipes); the dispatcher's own
/// behavior is covered without a pipe in <see cref="LocalApiDispatcherTests"/>.
/// </summary>
public class NamedPipeApiServerTests
{
    private static LocalApiDispatcher BuildDispatcher()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new SqlAgentDbContext(new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        var secrets = new InMemorySecretStore();
        var connections = new DatabaseConnectionService(db, secrets);
        var registry = new DatabaseProviderRegistry(Array.Empty<IDatabaseProvider>());
        var schema = new SchemaService(connections, registry, db);
        var queries = new QueryExecutionService(connections, registry, db);
        return new LocalApiDispatcher(
            connections,
            new ConnectionTester(connections, registry),
            schema,
            queries,
            new TablePolicyService(connections, registry, db),
            new NlQueryService(connections, schema, queries, new UnusedGateway()),
            new LocalTokenAuthenticator(secrets, NullLogger<LocalTokenAuthenticator>.Instance));
    }

    [Fact]
    public async Task Round_trips_a_request_over_the_pipe()
    {
        var pipeName = "SqlAgent.Test." + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = new NamedPipeApiServer(BuildDispatcher, pipeName);
        var serverTask = server.RunAsync(cts.Token);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cts.Token);
        using var reader = new StreamReader(client, leaveOpen: true);
        await using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };

        var request = JsonSerializer.Serialize(new { op = "list_databases" }, LocalApiContract.Json);
        await writer.WriteLineAsync(request.AsMemory(), cts.Token);
        var responseLine = await reader.ReadLineAsync(cts.Token);

        Assert.NotNull(responseLine);
        var response = JsonSerializer.Deserialize<LocalApiResponse>(responseLine!, LocalApiContract.Json)!;
        Assert.True(response.Ok);
        Assert.Equal(LocalApiContract.Version, response.Version);
        Assert.Empty(response.Data!.Value.Deserialize<List<DatabaseDto>>(LocalApiContract.Json)!);

        cts.Cancel();
        await serverTask; // cancellation unwinds the serve loop and RunAsync returns cleanly
    }
}
