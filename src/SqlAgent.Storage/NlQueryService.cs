using System.Text;
using SqlAgent.Core;

namespace SqlAgent.Storage;

/// <summary>Which of the three ask_database outcomes (CD-51 Story 1.4) a result carries.</summary>
public enum NlResponseKind { QueryResult, ClarificationRequired, Error }

/// <summary>
/// The ask_database contract: exactly one outcome per result. A <see cref="NlResponseKind.QueryResult"/>
/// carries the generated SQL plus the executed result set; <see cref="NlResponseKind.ClarificationRequired"/>
/// carries only a clarifying question (no SQL ran); <see cref="NlResponseKind.Error"/> carries a stable
/// <see cref="ErrorCode"/> and a user-safe message (never a stack trace), and still echoes the generated SQL
/// when one existed so the user can audit what was rejected.
/// </summary>
public record NlQueryResult(
    NlResponseKind Kind,
    string? GeneratedSql,
    string? ClarificationQuestion,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowCount,
    bool Truncated,
    long ElapsedMs)
{
    public static NlQueryResult Query(QueryExecutionResult r) => new(
        NlResponseKind.QueryResult, r.Sql, null, null, null,
        r.Columns, r.Rows, r.RowCount, r.Truncated, r.ElapsedMs);

    public static NlQueryResult Clarification(string question) => new(
        NlResponseKind.ClarificationRequired, null, question, null, null, [], [], 0, false, 0);

    public static NlQueryResult Error(string code, string message, string? generatedSql = null, long elapsedMs = 0) => new(
        NlResponseKind.Error, generatedSql, null, code, message, [], [], 0, false, elapsedMs);
}

/// <summary>
/// Turns a natural-language question into a validated SQL execution or a clarification (CD-51 Story 1.4).
/// The flow is fixed and fail-closed: resolve the connection, build a policy-filtered schema context, ask
/// the injectable <see cref="ILlmSqlGateway"/>, and — only if it returns SQL — run it through the SAME
/// <see cref="QueryExecutionService"/> every other surface uses, so parser/read-only/hidden-table policy is
/// enforced and audited identically. Ambiguous questions return clarification_required and never touch the
/// database. Internal failures (schema read, LLM call) collapse to stable error codes with user-safe
/// messages, not exception text.
/// </summary>
public class NlQueryService(
    DatabaseConnectionService connections,
    SchemaService schemas,
    QueryExecutionService executor,
    ILlmSqlGateway gateway)
{
    public async Task<NlQueryResult> AskAsync(Guid connectionId, string question, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return NlQueryResult.Error("question_empty", "No question was provided.");

        var info = await connections.GetAsync(connectionId, ct);
        if (info is null)
            return NlQueryResult.Error("connection_not_found", "No such database connection.");

        DatabaseSchema? schema;
        try
        {
            schema = await schemas.GetVisibleSchemaAsync(connectionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallow the provider/driver message — it can leak schema/host detail — and return a stable code.
            return NlQueryResult.Error("schema_extraction_error", "Could not read the database schema.");
        }

        if (schema is null)
            return NlQueryResult.Error("connection_secret_missing", "Connection secret is missing.");

        // Dialect hints first so the model targets the right syntax (TOP vs LIMIT, GETDATE vs NOW, etc.),
        // then the policy-filtered schema. Both are plain prompt text — nothing here is executed.
        var schemaContext = $"{DialectHints.For(info.ProviderType)}\n\n{FormatSchema(schema)}";
        var request = new LlmSqlRequest(question, info.ProviderType, schemaContext);

        LlmSqlResponse llm;
        try
        {
            llm = await gateway.GenerateSqlAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // Cancellation/timeout is the caller's signal, not an LLM failure.
        }
        catch (NotSupportedException)
        {
            // The documented signal a gateway throws to mean "no provider is configured" (see
            // UnavailableLlmSqlGateway in Program.cs), kept distinct from llm_error below so that once a
            // real provider is wired, its own timeouts/network errors/malformed responses still surface
            // as a genuine failure rather than being mistaken for "nothing is configured".
            return NlQueryResult.Error("llm_not_configured", "No LLM provider is configured on this server.");
        }
        catch (Exception)
        {
            return NlQueryResult.Error("llm_error", "The language model could not process the request.");
        }

        if (llm.NeedsClarification)
            return NlQueryResult.Clarification(
                string.IsNullOrWhiteSpace(llm.ClarificationQuestion)
                    ? "The question is ambiguous. Could you clarify what you want to retrieve?"
                    : llm.ClarificationQuestion!);

        // Generated SQL is never trusted: it goes through the same validate-then-execute path as every other
        // surface, so read-only and hidden-table policy still apply and the run is audited.
        var r = await executor.ExecuteSqlAsync(connectionId, llm.Sql!, ct);
        return r.Success
            ? NlQueryResult.Query(r)
            : NlQueryResult.Error(r.ErrorCode!, r.ErrorMessage!, r.Sql, r.ElapsedMs);
    }

    /// <summary>
    /// Compact DDL text for the LLM, built only from the already-filtered schema (hidden tables are gone
    /// before this point). One line per table with columns/types/nullability, a PK line, and FK lines.
    /// Types carry their declared size (<c>varchar(20)</c>, <c>decimal(10,2)</c>) via
    /// <see cref="SchemaColumn.TypeText"/> — without it the model cannot size literals or predicates.
    /// ponytail: deliberately simple; caching and context-budget-aware compaction are CD-75's job.
    /// </summary>
    private static string FormatSchema(DatabaseSchema schema)
    {
        if (schema.Tables.Count == 0) return "(no tables are visible)";

        var sb = new StringBuilder();
        foreach (var t in schema.Tables)
        {
            var cols = string.Join(", ", t.Columns.Select(c => $"{c.Name} {c.TypeText}{(c.IsNullable ? "" : " NOT NULL")}"));
            sb.Append(t.Schema).Append('.').Append(t.Name).Append('(').Append(cols).Append(')').AppendLine();
            if (t.PrimaryKey.Count > 0)
                sb.Append("  PK: ").AppendLine(string.Join(", ", t.PrimaryKey));
            foreach (var fk in t.ForeignKeys)
                sb.Append("  FK: ").Append(fk.Column).Append(" -> ")
                  .Append(fk.ReferencedSchema).Append('.').Append(fk.ReferencedTable).Append('.').AppendLine(fk.ReferencedColumn);
        }
        return sb.ToString();
    }
}
