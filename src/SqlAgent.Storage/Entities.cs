using SqlAgent.Core;
using SqlAgent.Core.Policy;

namespace SqlAgent.Storage;

/// <summary>
/// A configured database connection. The connection string itself is never stored here —
/// only <see cref="ConnectionStringSecretRef"/>, a key into the <see cref="ISecretStore"/>.
/// </summary>
public class DatabaseConnection
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DatabaseProviderType ProviderType { get; set; }
    public string ConnectionStringSecretRef { get; set; } = "";
    public bool IsReadOnly { get; set; }
    public AllowedDdl AllowedDdl { get; set; } = AllowedDdl.None;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Per-table visibility / access policy for a connection.</summary>
public class TablePolicy
{
    public Guid Id { get; set; }
    public Guid DatabaseConnectionId { get; set; }
    public string SchemaName { get; set; } = "";
    public string TableName { get; set; } = "";
    public bool IsVisible { get; set; } = true;
    public bool CanRead { get; set; } = true;
    public bool CanWrite { get; set; }
}

/// <summary>Cached, filtered schema description metadata for a connection.</summary>
public class SchemaCache
{
    public Guid Id { get; set; }
    public Guid DatabaseConnectionId { get; set; }
    public string SchemaHash { get; set; } = "";
    public string FilteredSchemaJson { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
}

/// <summary>Audit record for a single query decision. Result rows are never stored.</summary>
public class QueryAuditLog
{
    public Guid Id { get; set; }
    public Guid DatabaseConnectionId { get; set; }
    public string RequestedSql { get; set; } = "";
    public string? NormalizedSql { get; set; }
    public string Decision { get; set; } = "";
    public string? DenyReason { get; set; }
    public int? RowCount { get; set; }
    public long? DurationMs { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Generic key/value application setting.</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "";
}

/// <summary>Encrypted secret blob, persisted in the same SQLite store. Managed by <see cref="ISecretStore"/>.</summary>
public class Secret
{
    public string Reference { get; set; } = "";
    public byte[] Cipher { get; set; } = [];
}
