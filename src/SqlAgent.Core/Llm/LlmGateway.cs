namespace SqlAgent.Core;

/// <summary>
/// What the orchestration service hands an LLM: the user's question, the dialect to target, and the
/// already policy-filtered schema text (hidden tables removed upstream — see CD-50 T4). The gateway sees
/// no connection strings, no secrets, and no hidden tables (CD-51 Story 1.4).
/// </summary>
public record LlmSqlRequest(string Question, DatabaseProviderType Provider, string SchemaContext);

/// <summary>
/// The model's answer: either SQL to validate-and-run, or a clarifying question when the request is too
/// ambiguous to turn into SQL. Exactly one is populated; <see cref="NeedsClarification"/> picks the branch.
/// </summary>
public record LlmSqlResponse(string? Sql, string? ClarificationQuestion)
{
    public static LlmSqlResponse Generated(string sql) => new(sql, null);
    public static LlmSqlResponse Clarify(string question) => new(null, question);

    /// <summary>True when no SQL was produced — the caller must return clarification_required, not execute.</summary>
    public bool NeedsClarification => string.IsNullOrWhiteSpace(Sql);
}

/// <summary>
/// Provider-neutral seam to whichever LLM generates SQL (ADR pending model selection, CD-51). Kept as an
/// injectable abstraction so the orchestration service is testable with a fake and stays decoupled from any
/// vendor SDK. Implementations translate a question + schema context into SQL or a clarifying question; they
/// never execute SQL — execution always goes back through the policy-validated path.
/// </summary>
/// <remarks>
/// To signal that no provider is configured at all (as opposed to a configured provider's call failing),
/// throw <see cref="NotSupportedException"/>; <c>NlQueryService</c> maps that specifically to the
/// <c>llm_not_configured</c> error code, distinct from the <c>llm_error</c> code used for every other
/// exception. See <c>UnavailableLlmSqlGateway</c> in <c>Program.cs</c> for the reference implementation.
/// </remarks>
public interface ILlmSqlGateway
{
    Task<LlmSqlResponse> GenerateSqlAsync(LlmSqlRequest request, CancellationToken ct = default);
}
