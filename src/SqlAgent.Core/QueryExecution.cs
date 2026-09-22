using System.Data.Common;
using SqlAgent.Core.Policy;

namespace SqlAgent.Core;

/// <summary>
/// Limits applied to one execution (CD-50 T6). <see cref="Timeout"/> bounds wall-clock time;
/// <see cref="MaxRows"/> caps the rows materialized — the (MaxRows+1)th row only sets the truncated flag.
/// </summary>
public record QueryExecutionOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxRows { get; init; } = 1000;

    /// <summary>Native command timeout (seconds, min 1) — a backstop behind the linked cancellation token.</summary>
    public int CommandTimeoutSeconds => Math.Max(1, (int)Math.Ceiling(Timeout.TotalSeconds));

    public static QueryExecutionOptions Default { get; } = new();
}

/// <summary>Raw rows a provider read back: column names, row values, and whether more rows were left unread.</summary>
public record QueryResultSet(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated);

/// <summary>
/// The CD-50 query result contract returned to API callers. Carries the SQL, the result metadata
/// (columns, rows, row_count, truncated, elapsed_ms), and — on failure — a stable error code.
/// </summary>
public record QueryExecutionResult(
    string Sql,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowCount,
    bool Truncated,
    long ElapsedMs,
    DdlOperation? Operation = null)
{
    public static QueryExecutionResult Ok(
        string sql, QueryResultSet set, long elapsedMs)
        => new(sql, true, null, null, set.Columns, set.Rows, set.Rows.Count, set.Truncated, elapsedMs);

    public static QueryExecutionResult Failure(
        string sql,
        string errorCode,
        string message,
        long elapsedMs = 0,
        DdlOperation? operation = null)
        => new(sql, false, errorCode, message, [], [], 0, false, elapsedMs, operation);
}

/// <summary>
/// Reads a result set off any ADO.NET reader, stopping one row past <paramref name="maxRows"/> so the
/// row cap is enforced at the source (no unbounded buffering). Shared by every provider so truncation
/// behaves identically across dialects and stays unit-testable without a live server.
/// </summary>
public static class ResultSetReader
{
    public static async Task<QueryResultSet> ReadAsync(DbDataReader reader, int maxRows, CancellationToken ct)
    {
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<IReadOnlyList<object?>>();
        var truncated = false;

        while (await reader.ReadAsync(ct))
        {
            if (rows.Count >= maxRows) { truncated = true; break; }
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return new QueryResultSet(columns, rows, truncated);
    }
}
