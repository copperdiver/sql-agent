using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlAgent.Core;
using SqlAgent.Core.Policy;

namespace SqlAgent.Storage;

/// <summary>
/// Runs policy-approved SQL end to end (CD-50 T6): validate against the connection's policy and table
/// visibility, execute with a timeout / row cap / cancellation, and write one <see cref="QueryAuditLog"/>
/// row — decision, deny reason, normalized SQL, row count, duration — never the result rows themselves.
/// </summary>
public class QueryExecutionService(
    DatabaseConnectionService connections,
    IDatabaseProviderRegistry providers,
    SqlAgentDbContext db,
    ILogger<QueryExecutionService> logger,
    QueryExecutionOptions? options = null)
{
    private readonly QueryExecutionOptions _options = options ?? QueryExecutionOptions.Default;

    public async Task<QueryExecutionResult> ExecuteSqlAsync(Guid connectionId, string sql, CancellationToken ct = default)
    {
        var info = await connections.GetAsync(connectionId, ct);
        if (info is null)
            // No connection row exists, so there is nothing to audit against — return the error directly.
            return QueryExecutionResult.Failure(sql, "connection_not_found", "No such database connection.");

        var isVisible = await BuildVisibilityAsync(connectionId, ct);
        var decision = SqlPolicyValidator.Validate(sql, info.ProviderType, info.IsReadOnly, isVisible);

        if (!decision.Allowed)
        {
            await AuditAsync(connectionId, sql, decision.NormalizedSql, "deny", decision.Reason, null, null);
            return QueryExecutionResult.Failure(sql, decision.DenyCode!, decision.Reason!);
        }

        var connectionString = await connections.ResolveConnectionStringAsync(connectionId, ct);
        if (connectionString is null)
        {
            const string msg = "Connection secret is missing.";
            await AuditAsync(connectionId, sql, decision.NormalizedSql, "error", msg, null, null);
            return QueryExecutionResult.Failure(sql, "connection_secret_missing", msg);
        }

        var provider = providers.Get(info.ProviderType);
        var sw = Stopwatch.StartNew();

        // The linked source owns the timeout, so a tripped timeout and a caller-cancel both surface as
        // OperationCanceledException; we tell them apart by which token is actually cancelled.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.Timeout);

        try
        {
            var set = await provider.ExecuteQueryAsync(connectionString, sql, _options, timeoutCts.Token);
            sw.Stop();
            await AuditAsync(connectionId, sql, decision.NormalizedSql, "allow", null, set.Rows.Count, sw.ElapsedMilliseconds);
            return QueryExecutionResult.Ok(sql, set, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var (code, msg) = ct.IsCancellationRequested
                ? ("execution_canceled", "Query was canceled.")
                : ("execution_timeout", $"Query exceeded the {_options.Timeout.TotalSeconds:0.##}s timeout.");
            await AuditAsync(connectionId, sql, decision.NormalizedSql, "error", msg, null, sw.ElapsedMilliseconds);
            return QueryExecutionResult.Failure(sql, code, msg, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // The driver's own exception text can echo a connection string or other server detail, so it
            // is never returned or audited verbatim — it goes to the server log only. Both the audit row
            // and the caller (from where it flows into the persisted chat transcript) get the same fixed,
            // user-safe sentence instead.
            logger.LogError(ex, "Query execution failed for connection {ConnectionId}.", connectionId);
            const string message = "The query could not be executed. The details are in the server log.";
            await AuditAsync(connectionId, sql, decision.NormalizedSql, "error", message, null, sw.ElapsedMilliseconds);
            return QueryExecutionResult.Failure(sql, "execution_error", message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// A table is hidden if a TablePolicy marks it invisible. Matching is fail-closed for unqualified SQL:
    /// a bare table name is hidden if any schema's same-named table is hidden; a schema-qualified name must
    /// match the policy's schema too. (Tables with no policy row default to visible — same as the schema
    /// description path.)
    /// </summary>
    private async Task<Func<SqlTableReference, bool>> BuildVisibilityAsync(Guid connectionId, CancellationToken ct)
    {
        var hidden = await db.TablePolicies
            .Where(p => p.DatabaseConnectionId == connectionId && !p.IsVisible)
            .Select(p => new { p.SchemaName, p.TableName })
            .ToListAsync(ct);

        return t => !hidden.Any(h =>
            string.Equals(h.TableName, t.Name, StringComparison.OrdinalIgnoreCase) &&
            (t.Schema is null || string.Equals(h.SchemaName, t.Schema, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task AuditAsync(
        Guid connectionId, string sql, string? normalizedSql,
        string decision, string? denyReason, int? rowCount, long? durationMs)
    {
        db.QueryAuditLogs.Add(new QueryAuditLog
        {
            Id = Guid.NewGuid(),
            DatabaseConnectionId = connectionId,
            RequestedSql = sql,
            NormalizedSql = normalizedSql,
            Decision = decision,
            DenyReason = denyReason,
            RowCount = rowCount,
            DurationMs = durationMs,
            CreatedAt = DateTime.UtcNow,
        });
        // Always persist the audit, even when the caller's token cancelled the query above.
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
