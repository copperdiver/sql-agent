using SqlAgent.Core;

namespace SqlAgent.Tests;

/// <summary>
/// LLM gateway double for the chat tab's integration tests. Most tests just want an immediate canned
/// response or a thrown exception (mirrors <c>NlQueryServiceTests</c>' FakeGateway). The in-flight/guard
/// tests need genuine control over when <see cref="GenerateSqlAsync"/> resumes — unlike the SQL tab's
/// provider stub (which blocks on a real CancellationToken it can eventually observe via the Cancel
/// button), the chat tab has no cancel action and Workspace calls NlQueryService.AskAsync without a
/// token, so blocking via Task.Delay(Timeout.Infinite, ct) would hang forever with nothing to cancel it.
/// Hold()/Release() give the test explicit, deterministic control instead.
/// </summary>
sealed class ChatGatewayStub : ILlmSqlGateway
{
    public LlmSqlResponse? NextResponse { get; set; }
    public int CallCount => _calls;

    private int _calls;
    private TaskCompletionSource<LlmSqlResponse>? _gate;

    public void Hold() => _gate = new TaskCompletionSource<LlmSqlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release(LlmSqlResponse response) => _gate?.SetResult(response);

    public Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        if (_gate is { } gate) return gate.Task;
        return Task.FromResult(NextResponse ?? LlmSqlResponse.Generated("SELECT 1"));
    }
}
