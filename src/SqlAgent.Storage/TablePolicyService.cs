using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;
using SqlAgent.Core.Policy;

namespace SqlAgent.Storage;

/// <summary>Whether a live object is a base table or a view. Derived from the extracted schema on every
/// read — it is deliberately not a stored column, because nothing would read it back.</summary>
public enum DatabaseObjectKind
{
    Table,
    View,
}

/// <summary>One live object with its effective access level for a connection.</summary>
public record DatabaseObjectPolicy(
    string Schema, string Name, DatabaseObjectKind Kind, ObjectAccess Access);

/// <summary>Why a write did or did not happen. A bool cannot tell "no such connection" from "a view
/// cannot be writable", and the config page needs different words for each.</summary>
public enum SetAccessOutcome
{
    Applied,
    ConnectionMissing,
    ViewCannotBeWritable,
}

/// <summary>
/// Read/write side of per-object access (CD-50, extended in Phase C1). <see cref="SchemaService"/> only
/// ever exposes the already-filtered schema, so the config page needs this to see <em>every</em> live
/// object — hidden ones included — and set its level. An object with no <see cref="TablePolicy"/> row is
/// fully accessible; that is the rule every layer in this codebase applies, and it is why the store holds
/// rows only for objects somebody has actually decided about.
/// </summary>
public class TablePolicyService(
    DatabaseConnectionService connections, IDatabaseProviderRegistry providers, SqlAgentDbContext db)
{
    /// <summary>Every live table and view with its effective level, or null if the connection or its
    /// secret is missing. Ordered by schema then name, so the page can group without re-sorting.</summary>
    public async Task<IReadOnlyList<DatabaseObjectPolicy>?> ListObjectsAsync(
        Guid connectionId, CancellationToken ct = default)
    {
        var schema = await ExtractAsync(connectionId, ct);
        if (schema is null) return null;

        var byKey = await LoadPoliciesAsync(connectionId, ct);

        var tables = schema.Tables.Select(t => new DatabaseObjectPolicy(
            t.Schema, t.Name, DatabaseObjectKind.Table, AccessOf(byKey, t.Schema, t.Name)));
        var views = schema.ViewList.Select(v => new DatabaseObjectPolicy(
            v.Schema, v.Name, DatabaseObjectKind.View, AccessOf(byKey, v.Schema, v.Name)));

        return tables.Concat(views)
            .OrderBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Upserts one object's level, on every call — including <see cref="ObjectAccess.Full"/>, so a level
    /// the user chose is a row in the store rather than something inferred from a row's absence.
    /// <paramref name="kind"/> is an argument rather than a stored column: its only job is the view
    /// refusal below.
    /// </summary>
    public async Task<SetAccessOutcome> SetAccessAsync(
        Guid connectionId, string schema, string name,
        DatabaseObjectKind kind, ObjectAccess access, CancellationToken ct = default)
    {
        if (await connections.GetAsync(connectionId, ct) is null)
            return SetAccessOutcome.ConnectionMissing;

        if (kind == DatabaseObjectKind.View && access == ObjectAccess.Full)
            return SetAccessOutcome.ViewCannotBeWritable;

        await UpsertAsync(connectionId, schema, name, access, ct);
        await db.SaveChangesAsync(ct);
        await InvalidateAsync(connectionId, ct);
        return SetAccessOutcome.Applied;
    }

    /// <summary>
    /// Sets every object under one schema at once — the schema header row on the config page. A view in
    /// the batch is clamped to <see cref="ObjectAccess.ReadOnly"/> rather than failing the whole call:
    /// "everything here, as far as each object is allowed to go" is what the header means, and refusing
    /// the batch because one member is a view would make it useless on any schema that has one.
    /// </summary>
    public async Task<SetAccessOutcome> SetSchemaAccessAsync(
        Guid connectionId, string schema, ObjectAccess access, CancellationToken ct = default)
    {
        var objects = await ListObjectsAsync(connectionId, ct);
        if (objects is null) return SetAccessOutcome.ConnectionMissing;

        foreach (var o in objects.Where(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase)))
        {
            var level = o.Kind == DatabaseObjectKind.View && access == ObjectAccess.Full
                ? ObjectAccess.ReadOnly
                : access;
            await UpsertAsync(connectionId, o.Schema, o.Name, level, ct);
        }

        await db.SaveChangesAsync(ct);
        await InvalidateAsync(connectionId, ct);
        return SetAccessOutcome.Applied;
    }

    private async Task<DatabaseSchema?> ExtractAsync(Guid connectionId, CancellationToken ct)
    {
        var info = await connections.GetAsync(connectionId, ct);
        if (info is null) return null;
        var connectionString = await connections.ResolveConnectionStringAsync(connectionId, ct);
        if (connectionString is null) return null;
        return await providers.Get(info.ProviderType).GetSchemaAsync(connectionString, ct);
    }

    private async Task<Dictionary<(string, string), TablePolicy>> LoadPoliciesAsync(
        Guid connectionId, CancellationToken ct)
        => (await db.TablePolicies.Where(p => p.DatabaseConnectionId == connectionId).ToListAsync(ct))
            .ToDictionary(p => (p.SchemaName, p.TableName));

    /// <summary>
    /// The one place the three levels are read back out of the two columns. No row means full access;
    /// invisible outranks everything; visible-but-not-writable is read-only. CanRead is not consulted —
    /// no path writes it false, and treating it as an axis would invent a fourth state the UI cannot
    /// produce or display.
    /// </summary>
    private static ObjectAccess AccessOf(
        Dictionary<(string, string), TablePolicy> byKey, string schema, string name)
        => !byKey.TryGetValue((schema, name), out var p) ? ObjectAccess.Full
            : !p.IsVisible ? ObjectAccess.Hidden
            : p.CanWrite ? ObjectAccess.Full
            : ObjectAccess.ReadOnly;

    private async Task UpsertAsync(
        Guid connectionId, string schema, string name, ObjectAccess access, CancellationToken ct)
    {
        var policy = await db.TablePolicies.FirstOrDefaultAsync(
            p => p.DatabaseConnectionId == connectionId && p.SchemaName == schema && p.TableName == name, ct);
        if (policy is null)
        {
            policy = new TablePolicy
            {
                Id = Guid.NewGuid(),
                DatabaseConnectionId = connectionId,
                SchemaName = schema,
                TableName = name,
            };
            db.TablePolicies.Add(policy);
        }

        policy.IsVisible = access != ObjectAccess.Hidden;
        policy.CanRead = true;
        policy.CanWrite = access == ObjectAccess.Full;
    }

    /// <summary>Drops any cached schema so a now-hidden object cannot survive in a stale entry (CD-51
    /// Story 1.5). Called from every write path, not from the shared upsert, so a future caller that
    /// batches writes cannot accidentally skip it.</summary>
    private async Task InvalidateAsync(Guid connectionId, CancellationToken ct)
        => await db.SchemaCaches.Where(c => c.DatabaseConnectionId == connectionId).ExecuteDeleteAsync(ct);
}
