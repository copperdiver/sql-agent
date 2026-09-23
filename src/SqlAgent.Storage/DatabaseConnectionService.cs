using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;
using SqlAgent.Core.Policy;

namespace SqlAgent.Storage;

/// <summary>Fields a caller supplies when creating or updating a connection (no secret).</summary>
public record DatabaseConnectionInput(string Name, DatabaseProviderType ProviderType, bool IsReadOnly);

/// <summary>Read model for a connection. Deliberately omits the connection-string secret.</summary>
public record DatabaseConnectionInfo(
    Guid Id,
    string Name,
    DatabaseProviderType ProviderType,
    bool IsReadOnly,
    AllowedDdl AllowedDdl,
    bool HasSecret,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// CRUD for database connection configuration. Secret connection strings are written through
/// <see cref="ISecretStore"/> and are never returned by the read API — use
/// <see cref="ResolveConnectionStringAsync"/> for the explicit, internal secret read.
/// </summary>
public class DatabaseConnectionService(SqlAgentDbContext db, ISecretStore secrets)
{
    public async Task<DatabaseConnectionInfo> CreateAsync(
        DatabaseConnectionInput input, string connectionString, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var entity = new DatabaseConnection
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            ProviderType = input.ProviderType,
            ConnectionStringSecretRef = $"db:{Guid.NewGuid():N}",
            IsReadOnly = input.IsReadOnly,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await secrets.SetAsync(entity.ConnectionStringSecretRef, connectionString, ct);
        db.DatabaseConnections.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToInfo(entity);
    }

    public async Task<DatabaseConnectionInfo?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.DatabaseConnections.FindAsync([id], ct);
        return e is null ? null : ToInfo(e);
    }

    public async Task<DatabaseConnectionInfo?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var e = await db.DatabaseConnections.FirstOrDefaultAsync(x => x.Name == name, ct);
        return e is null ? null : ToInfo(e);
    }

    public async Task<IReadOnlyList<DatabaseConnectionInfo>> ListAsync(CancellationToken ct = default)
        => await db.DatabaseConnections.OrderBy(x => x.Name)
            .Select(e => ToInfo(e)).ToListAsync(ct);

    /// <summary>Updates config fields. Pass a non-null <paramref name="connectionString"/> to rotate the secret.</summary>
    public async Task<DatabaseConnectionInfo?> UpdateAsync(
        Guid id, DatabaseConnectionInput input, string? connectionString = null, CancellationToken ct = default)
    {
        var e = await db.DatabaseConnections.FindAsync([id], ct);
        if (e is null) return null;
        e.Name = input.Name;
        e.ProviderType = input.ProviderType;
        e.IsReadOnly = input.IsReadOnly;
        e.UpdatedAt = DateTime.UtcNow;
        if (connectionString is not null)
            await secrets.SetAsync(e.ConnectionStringSecretRef, connectionString, ct);
        await db.SaveChangesAsync(ct);
        return ToInfo(e);
    }

    public async Task<DatabaseConnectionInfo?> SetAllowedDdlAsync(
        Guid id, AllowedDdl allowedDdl, CancellationToken ct = default)
    {
        var e = await db.DatabaseConnections.FindAsync([id], ct);
        if (e is null) return null;
        e.AllowedDdl = allowedDdl;
        e.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToInfo(e);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.DatabaseConnections.FindAsync([id], ct);
        if (e is null) return false;
        await secrets.DeleteAsync(e.ConnectionStringSecretRef, ct);
        db.DatabaseConnections.Remove(e);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Explicit secret read for internal callers (providers, executor). Not part of the read DTO.</summary>
    public async Task<string?> ResolveConnectionStringAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.DatabaseConnections.FindAsync([id], ct);
        return e is null ? null : await secrets.GetAsync(e.ConnectionStringSecretRef, ct);
    }

    private static DatabaseConnectionInfo ToInfo(DatabaseConnection e) => new(
        e.Id, e.Name, e.ProviderType, e.IsReadOnly,
        e.AllowedDdl,
        HasSecret: !string.IsNullOrEmpty(e.ConnectionStringSecretRef),
        e.CreatedAt, e.UpdatedAt);
}
