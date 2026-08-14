using Microsoft.Extensions.Logging;
using SqlAgent.Core;

namespace SqlAgent.Storage;

/// <summary>
/// Tests database connections by selecting the provider from the stored/declared
/// <see cref="DatabaseProviderType"/>. Draft = caller-supplied string; saved = resolved from the secret
/// store.
///
/// This class is the boundary the driver's own text does not cross. It takes a
/// <see cref="ConnectionTestResult"/>, writes its <c>Diagnostic</c> to the log, and returns a
/// <see cref="ConnectionTestOutcome"/> — a type with nowhere to put the text. The parent spec has said
/// since phase A that provider exception text is never rendered; before this it was a rule enforced by a
/// comment, and the comment turned out to describe something the code did not do.
/// </summary>
public class ConnectionTester(
    DatabaseConnectionService connections,
    IDatabaseProviderRegistry providers,
    ILogger<ConnectionTester> logger)
{
    /// <summary>Tests an unsaved connection string against the given provider type.</summary>
    public async Task<ConnectionTestOutcome> TestDraftAsync(
        DatabaseProviderType providerType, string connectionString, CancellationToken ct = default)
        => Cross(await providers.Get(providerType).TestConnectionAsync(connectionString, ct), null);

    /// <summary>Tests a saved connection by id: resolves its secret and uses its stored provider type.
    /// Null when the connection or its secret is missing.</summary>
    public async Task<ConnectionTestOutcome?> TestSavedAsync(Guid id, CancellationToken ct = default)
    {
        var info = await connections.GetAsync(id, ct);
        if (info is null) return null;
        var connectionString = await connections.ResolveConnectionStringAsync(id, ct);
        if (connectionString is null) return null;
        return Cross(await providers.Get(info.ProviderType).TestConnectionAsync(connectionString, ct), id);
    }

    private ConnectionTestOutcome Cross(ConnectionTestResult result, Guid? connectionId)
    {
        if (!result.Success && !string.IsNullOrEmpty(result.Diagnostic))
        {
            // Warning, not Error: a rejecting server is an ordinary outcome for a tool pointed at
            // arbitrary databases, but it is the one thing anyone will want out of the log afterwards.
            logger.LogWarning(
                "Connection test failed for {Connection} ({Failure}). The driver reported: {Diagnostic}",
                connectionId?.ToString() ?? "a draft connection", result.Failure, result.Diagnostic);
        }

        return new ConnectionTestOutcome(
            result.Success, result.Failure, result.ServerVersion, result.ElapsedMs);
    }
}
