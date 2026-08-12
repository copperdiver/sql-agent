using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SqlAgent.Storage;

/// <summary>
/// How `dotnet ef` builds a context at design time. Without it EF falls back to the startup project's
/// entry point through HostFactoryResolver, which means scaffolding a migration depends on the whole web
/// host booting far enough to call builder.Build() — including UseStaticWebAssets, the Windows-service
/// registrations and the loopback URL resolution, none of which have anything to do with the model. The
/// connection string here is never opened by `migrations add`; it exists because DbContextOptions demands
/// one, and it deliberately points at a scratch file rather than the real store so a mistyped `database
/// update` cannot touch anyone's data.
/// </summary>
public sealed class SqlAgentDbContextFactory : IDesignTimeDbContextFactory<SqlAgentDbContext>
{
    public SqlAgentDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SqlAgentDbContext>()
            .UseSqlite("Data Source=sqlagent-design-time.db")
            .Options);
}
