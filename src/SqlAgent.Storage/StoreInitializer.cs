using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace SqlAgent.Storage;

/// <summary>
/// Brings the local store up to the current schema at startup.
///
/// The hard case is the store that already exists. Until Phase B1 the host called EnsureCreatedAsync,
/// which creates tables and records nothing — so a store in the field has the six original tables and no
/// __EFMigrationsHistory at all. MigrateAsync against one of those replays InitialCreate, hits
/// "table DatabaseConnections already exists" and throws on startup. The fix is to write the history row
/// the old code path never wrote, exactly once, and only for a store that is genuinely in that state.
/// </summary>
public static class StoreInitializer
{
    private const string HistoryTable = "__EFMigrationsHistory";

    /// <summary>Any table only the pre-migration schema could have created. Its presence, together with
    /// a missing history table, is what identifies an EnsureCreated store.</summary>
    private const string LegacyMarkerTable = "DatabaseConnections";

    public static async Task InitializeAsync(
        SqlAgentDbContext db, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            var tables = await ListTablesAsync(db, ct);

            if (!tables.Contains(HistoryTable) && tables.Contains(LegacyMarkerTable))
            {
                // GetMigrations() returns the migrations compiled into this assembly in id order, so the
                // first is InitialCreate. Reading it here rather than hardcoding the timestamped id keeps
                // this correct if the migration is ever regenerated.
                var baseline = db.Database.GetMigrations().First();
                logger.LogInformation(
                    "Store predates migrations; stamping {Migration} as applied before migrating.", baseline);
                await StampAsync(db, baseline, ct);
            }

            await db.Database.MigrateAsync(ct);
        }
        catch (Exception ex)
        {
            // A half-migrated store loses or corrupts data on every subsequent write, so this is fatal by
            // design (see the spec's decision table). The connection string is logged because the first
            // question anyone asks is "which store?" — it is a local file path, not a secret; the
            // database passwords live in ISecretStore and never appear here.
            logger.LogError(ex, "The local store at {Store} could not be migrated. The host cannot start.",
                db.Database.GetDbConnection().ConnectionString);
            throw;
        }
    }

    /// <summary>Table names in the SQLite file. The alias to "Value" is required: EF's scalar SqlQueryRaw
    /// projects a single column by that name and throws otherwise.</summary>
    private static async Task<HashSet<string>> ListTablesAsync(SqlAgentDbContext db, CancellationToken ct)
    {
        var names = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync(ct);
        return names.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Writes the history table and one row, using EF's own scripts rather than hand-rolled DDL
    /// so the table shape is whatever this EF version expects to read back.</summary>
    private static async Task StampAsync(SqlAgentDbContext db, string migrationId, CancellationToken ct)
    {
        var history = db.GetService<IHistoryRepository>();
        var version = typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "10.0.0";

        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
        await db.Database.ExecuteSqlRawAsync(
            history.GetInsertScript(new HistoryRow(migrationId, version)), ct);
    }
}
