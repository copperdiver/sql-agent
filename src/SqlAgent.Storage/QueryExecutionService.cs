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
    SchemaService schemas,
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

        var resolve = await TryBuildPolicyResolverAsync(connectionId, ct);
        if (resolve is null)
        {
            const string msg = "The database schema could not be read, so this query could not be checked.";
            await AuditAsync(connectionId, sql, null, "deny", msg, null, null);
            return QueryExecutionResult.Failure(sql, "schema_unavailable", msg);
        }

        var decision = SqlPolicyValidator.Validate(sql, info.ProviderType, info.IsReadOnly, resolve);

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
    /// Builds the resolver the policy asks about every referenced object, or null when the schema could
    /// not be read.
    ///
    /// Two sources. Levels come from this connection's TablePolicy rows; an object with no row is fully
    /// accessible, which is the rule every layer here applies. Whether an object is a view comes from the
    /// schema, not from a policy row — given that default, a view nobody has touched has no row to read,
    /// so the policy table simply cannot answer the question. The cached copy is used when it exists, and
    /// that it is already visibility-filtered costs nothing: a hidden object is refused by the visibility
    /// branch before the view branch is reached.
    ///
    /// Matching is fail-closed for unqualified SQL. A bare name takes the most restrictive level among
    /// same-named objects across every schema, and counts as a view if any of them is one; a
    /// schema-qualified name must match the schema too. The consequence is worth knowing: a bare name
    /// matching a table in one schema and a view in another is treated as a view, and a write to it is
    /// refused. That is the same trade the hidden-object rule has always made.
    ///
    /// Returning null rather than throwing keeps ExecuteSqlAsync's contract — it answers with a result,
    /// never an exception — and denying is the fail-closed answer: without the schema a view cannot be
    /// told from a table, and allowing the query would let a write to a view through unchecked.
    /// </summary>
    private async Task<Func<SqlTableReference, ObjectPolicy>?> TryBuildPolicyResolverAsync(
        Guid connectionId, CancellationToken ct)
    {
        var rows = await db.TablePolicies
            .Where(p => p.DatabaseConnectionId == connectionId)
            .Select(p => new { p.SchemaName, p.TableName, p.IsVisible, p.CanWrite })
            .ToListAsync(ct);

        IReadOnlyList<SchemaView> views;
        try
        {
            var schema = await schemas.GetOrRefreshAsync(connectionId, ct);
            if (schema is null) return null;
            views = schema.ViewList;
        }
        catch (Exception ex)
        {
            // The provider's own text can echo a connection string, so it goes to the log and nowhere
            // else — the caller gets the fixed sentence at the call site above.
            logger.LogError(ex, "The schema for connection {ConnectionId} could not be read.", connectionId);
            return null;
        }

        return t =>
        {
            bool Matches(string schemaName, string objectName) =>
                string.Equals(objectName, t.Name, StringComparison.OrdinalIgnoreCase) &&
                (t.Schema is null || string.Equals(schemaName, t.Schema, StringComparison.OrdinalIgnoreCase));

            var matched = rows.Where(r => Matches(r.SchemaName, r.TableName)).ToList();

            // Most restrictive wins. No matching row at all means nobody has decided anything about this
            // object, which is full access.
            var access = matched.Count == 0
                ? ObjectAccess.Full
                : matched.Min(r => !r.IsVisible ? ObjectAccess.Hidden
                    : r.CanWrite ? ObjectAccess.Full
                    : ObjectAccess.ReadOnly);

            var isView = views.Any(v => Matches(v.Schema, v.Name));

            return new ObjectPolicy(access, isView);
        };
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
