using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqlAgent.Core;
using SqlAgent.Core.Policy;
using SqlAgent.Storage;

namespace SqlAgent.Tests;

public class DatabaseConnectionServiceTests
{
    // Each test gets an isolated in-memory SQLite DB. The connection must stay open for the
    // duration, so the test owns it and disposes at the end.
    private static (SqlAgentDbContext db, SqliteConnection conn) NewStore()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new SqlAgentDbContext(
            new DbContextOptionsBuilder<SqlAgentDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    [Fact]
    public async Task Create_then_Get_returns_config_without_secret()
    {
        var (db, conn) = NewStore();
        var svc = new DatabaseConnectionService(db, new InMemorySecretStore());

        var created = await svc.CreateAsync(
            new DatabaseConnectionInput("prod", DatabaseProviderType.Postgres, IsReadOnly: true),
            "Host=db;Username=u;Password=secret");

        var loaded = await svc.GetAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("prod", loaded!.Name);
        Assert.Equal(DatabaseProviderType.Postgres, loaded.ProviderType);
        Assert.True(loaded.IsReadOnly);
        Assert.Equal(AllowedDdl.None, loaded.AllowedDdl);
        Assert.True(loaded.HasSecret);
        // The read DTO must not carry the secret anywhere.
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(loaded));

        conn.Dispose();
    }

    [Fact]
    public async Task SetAllowedDdl_round_trips_flags_without_touching_the_secret()
    {
        var (db, conn) = NewStore();
        var secrets = new InMemorySecretStore();
        var svc = new DatabaseConnectionService(db, secrets);
        var created = await svc.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.Postgres, false), "keep-me");

        var updated = await svc.SetAllowedDdlAsync(
            created.Id, AllowedDdl.CreateTable | AllowedDdl.DropIndex | AllowedDdl.Truncate);

        Assert.NotNull(updated);
        Assert.Equal(AllowedDdl.CreateTable | AllowedDdl.DropIndex | AllowedDdl.Truncate, updated!.AllowedDdl);
        Assert.Equal("keep-me", await svc.ResolveConnectionStringAsync(created.Id));
        conn.Dispose();
    }

    [Fact]
    public async Task SetAllowedDdl_for_an_unknown_connection_is_graceful()
    {
        var (db, conn) = NewStore();
        var svc = new DatabaseConnectionService(db, new InMemorySecretStore());

        Assert.Null(await svc.SetAllowedDdlAsync(Guid.NewGuid(), AllowedDdl.DropTable));

        conn.Dispose();
    }

    [Fact]
    public async Task ResolveConnectionString_returns_stored_secret()
    {
        var (db, conn) = NewStore();
        var svc = new DatabaseConnectionService(db, new InMemorySecretStore());
        var created = await svc.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.SqlServer, false), "the-conn-string");

        Assert.Equal("the-conn-string", await svc.ResolveConnectionStringAsync(created.Id));

        conn.Dispose();
    }

    [Fact]
    public async Task List_returns_all_created_connections()
    {
        var (db, conn) = NewStore();
        var svc = new DatabaseConnectionService(db, new InMemorySecretStore());
        await svc.CreateAsync(new DatabaseConnectionInput("b", DatabaseProviderType.SqlServer, false), "x");
        await svc.CreateAsync(new DatabaseConnectionInput("a", DatabaseProviderType.Postgres, false), "y");

        var all = await svc.ListAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal(["a", "b"], all.Select(x => x.Name)); // ordered by name

        conn.Dispose();
    }

    [Fact]
    public async Task Update_changes_fields_and_can_rotate_secret()
    {
        var (db, conn) = NewStore();
        var secrets = new InMemorySecretStore();
        var svc = new DatabaseConnectionService(db, secrets);
        var created = await svc.CreateAsync(
            new DatabaseConnectionInput("old", DatabaseProviderType.SqlServer, false), "old-secret");

        var updated = await svc.UpdateAsync(
            created.Id,
            new DatabaseConnectionInput("new", DatabaseProviderType.Postgres, IsReadOnly: true),
            connectionString: "new-secret");

        Assert.NotNull(updated);
        Assert.Equal("new", updated!.Name);
        Assert.Equal(DatabaseProviderType.Postgres, updated.ProviderType);
        Assert.True(updated.IsReadOnly);
        Assert.Equal("new-secret", await svc.ResolveConnectionStringAsync(created.Id));

        conn.Dispose();
    }

    [Fact]
    public async Task Update_without_connectionString_keeps_existing_secret()
    {
        var (db, conn) = NewStore();
        var svc = new DatabaseConnectionService(db, new InMemorySecretStore());
        var created = await svc.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.SqlServer, false), "keep-me");

        await svc.UpdateAsync(created.Id, new DatabaseConnectionInput("renamed", DatabaseProviderType.SqlServer, false));

        Assert.Equal("keep-me", await svc.ResolveConnectionStringAsync(created.Id));

        conn.Dispose();
    }

    [Fact]
    public async Task Delete_removes_connection_and_its_secret()
    {
        var (db, conn) = NewStore();
        var svc = new DatabaseConnectionService(db, new InMemorySecretStore());
        var created = await svc.CreateAsync(
            new DatabaseConnectionInput("c", DatabaseProviderType.SqlServer, false), "s");

        var deleted = await svc.DeleteAsync(created.Id);

        Assert.True(deleted);
        Assert.Null(await svc.GetAsync(created.Id));
        Assert.Null(await svc.ResolveConnectionStringAsync(created.Id));

        conn.Dispose();
    }

    [Fact]
    public async Task Get_update_delete_unknown_id_are_graceful()
    {
        var (db, conn) = NewStore();
        var svc = new DatabaseConnectionService(db, new InMemorySecretStore());
        var missing = Guid.NewGuid();

        Assert.Null(await svc.GetAsync(missing));
        Assert.Null(await svc.UpdateAsync(missing, new DatabaseConnectionInput("x", DatabaseProviderType.SqlServer, false)));
        Assert.False(await svc.DeleteAsync(missing));

        conn.Dispose();
    }
}
