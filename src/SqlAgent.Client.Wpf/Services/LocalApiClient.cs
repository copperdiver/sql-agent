using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SqlAgent.Api.Local;

namespace SqlAgent.Client.Wpf.Services;

/// <summary>Thrown when the host returns an error envelope (or the pipe call cannot complete). Carries the
/// stable API <see cref="Code"/> so the UI can react without string-matching the message.</summary>
public sealed class LocalApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The client's single point of contact with the Core host (CD-50 T9 — "calls only the local Core API").
/// Each call opens a short-lived named-pipe connection, writes one newline-delimited JSON request, reads one
/// response, and maps an error envelope to <see cref="LocalApiException"/>. The wire DTOs come straight from
/// the shared contracts assembly, so the client and host can never drift on the shape.
/// </summary>
public class LocalApiClient(string pipeName = LocalApiContract.DefaultPipeName, string? authToken = null)
{
    private const int ConnectTimeoutMs = 3000;

    public Task<List<DatabaseDto>> ListDatabasesAsync(CancellationToken ct = default) =>
        CallAsync<List<DatabaseDto>>("list_databases", null, ct);

    public Task<DatabaseDto> SaveDatabaseAsync(SaveDatabaseParams p, CancellationToken ct = default) =>
        CallAsync<DatabaseDto>("save_database", p, ct);

    public Task<DeletedDto> DeleteDatabaseAsync(Guid id, CancellationToken ct = default) =>
        CallAsync<DeletedDto>("delete_database", new DeleteDatabaseParams(id), ct);

    public Task<ConnectionTestDto> TestConnectionAsync(TestConnectionParams p, CancellationToken ct = default) =>
        CallAsync<ConnectionTestDto>("test_connection", p, ct);

    public Task<SchemaDto> DescribeSchemaAsync(Guid id, CancellationToken ct = default) =>
        CallAsync<SchemaDto>("describe_schema", new DescribeSchemaParams(id), ct);

    public Task<QueryResultDto> ExecuteSqlAsync(Guid id, string sql, CancellationToken ct = default) =>
        CallAsync<QueryResultDto>("execute_sql", new ExecuteSqlParams(id, sql), ct);

    public Task<TablePoliciesDto> ListTablePoliciesAsync(Guid id, CancellationToken ct = default) =>
        CallAsync<TablePoliciesDto>("list_table_policies", new ListTablePoliciesParams(id), ct);

    public Task<TablePolicyDto> SetTablePolicyAsync(
        Guid id, string schema, string table, bool isVisible, CancellationToken ct = default) =>
        CallAsync<TablePolicyDto>("set_table_policy", new SetTablePolicyParams(id, schema, table, isVisible), ct);

    private async Task<T> CallAsync<T>(string op, object? @params, CancellationToken ct)
    {
        var response = await SendAsync(op, @params, ct);
        if (!response.Ok)
            throw new LocalApiException(response.Error?.Code ?? ApiErrorCodes.InternalError,
                response.Error?.Message ?? "Unknown host error.");
        return response.Data!.Value.Deserialize<T>(LocalApiContract.Json)
            ?? throw new LocalApiException(ApiErrorCodes.InternalError, "Host returned an empty result.");
    }

    private async Task<LocalApiResponse> SendAsync(string op, object? @params, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, ct);
        }
        catch (TimeoutException)
        {
            throw new LocalApiException(ApiErrorCodes.ConnectionFailed,
                $"The SQL Agent host is not running (no pipe '{pipeName}').");
        }

        var paramsElement = @params is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(@params, LocalApiContract.Json);
        var requestLine = JsonSerializer.Serialize(new LocalApiRequest(op, paramsElement, authToken), LocalApiContract.Json);

        // leaveOpen so disposing the reader/writer doesn't tear down the pipe before the other has flushed.
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);

        await writer.WriteLineAsync(requestLine.AsMemory(), ct);
        var responseLine = await reader.ReadLineAsync(ct);
        if (responseLine is null)
            throw new LocalApiException(ApiErrorCodes.ConnectionFailed, "The host closed the connection without responding.");

        return JsonSerializer.Deserialize<LocalApiResponse>(responseLine, LocalApiContract.Json)
            ?? throw new LocalApiException(ApiErrorCodes.InternalError, "The host sent an unreadable response.");
    }
}
