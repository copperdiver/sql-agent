using Microsoft.EntityFrameworkCore;

namespace SqlAgent.Storage;

/// <summary>Local SQLite configuration store for the SQL Agent (CD-50 ADR-0004).</summary>
public class SqlAgentDbContext(DbContextOptions<SqlAgentDbContext> options) : DbContext(options)
{
    public DbSet<DatabaseConnection> DatabaseConnections => Set<DatabaseConnection>();
    public DbSet<TablePolicy> TablePolicies => Set<TablePolicy>();
    public DbSet<SchemaCache> SchemaCaches => Set<SchemaCache>();
    public DbSet<QueryAuditLog> QueryAuditLogs => Set<QueryAuditLog>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Secret> Secrets => Set<Secret>();
    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatMessageDatabase> ChatMessageDatabases => Set<ChatMessageDatabase>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DatabaseConnection>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<TablePolicy>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.DatabaseConnectionId, x.SchemaName, x.TableName }).IsUnique();
        });
        b.Entity<SchemaCache>().HasKey(x => x.Id);
        b.Entity<QueryAuditLog>().HasKey(x => x.Id);
        b.Entity<AppSetting>().HasKey(x => x.Key);
        b.Entity<Secret>().HasKey(x => x.Reference);

        b.Entity<Chat>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.LastMessageAt);
        });
        b.Entity<ChatMessage>(e =>
        {
            e.HasKey(x => x.Id);
            // Unique, not merely indexed: it is the backstop for two browser tabs appending to the same
            // chat at once. ChatService catches the violation and retries rather than writing two
            // messages that both claim the same position.
            e.HasIndex(x => new { x.ChatId, x.Sequence }).IsUnique();
            e.HasOne(x => x.Chat).WithMany(c => c.Messages)
                .HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
            // Stored as text, like QueryAuditLog.Decision. An int column would silently re-interpret
            // every existing row the day someone inserts a member in the middle of either enum.
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.OutcomeKind).HasConversion<string>().HasMaxLength(32);
        });
        b.Entity<ChatMessageDatabase>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ChatMessageId);
            // One row per database per message. Names are unique across connections, so this also stops
            // the same database being attached twice — ChatService dedupes by name before writing, and
            // this index is the backstop that enforces it regardless.
            e.HasIndex(x => new { x.ChatMessageId, x.DatabaseName }).IsUnique();
            e.HasOne(x => x.Message).WithMany(m => m.Databases)
                .HasForeignKey(x => x.ChatMessageId).OnDelete(DeleteBehavior.Cascade);
            // No foreign key to DatabaseConnection on purpose: the reference is a soft one, and
            // deliberately not a live one — DatabaseConnectionId is never cleaned up when a connection
            // is renamed or deleted, so it is a historical value only, not proof the connection still
            // exists. A real FK with SetNull would keep the id trustworthy, but it would put a
            // constraint on a table that has nothing to do with chat and make
            // DatabaseConnectionService's delete path depend on chat schema. DatabaseName is what a
            // transcript actually relies on to say what a question was asked against.
        });
    }
}
