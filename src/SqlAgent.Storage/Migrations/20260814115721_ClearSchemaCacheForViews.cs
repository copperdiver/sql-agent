using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlAgent.Storage.Migrations
{
    /// <summary>
    /// A data migration, not a schema one. Phase C1 taught DatabaseSchema to carry views, which changed
    /// the shape of the JSON in SchemaCache. Every row written before that describes a database that
    /// appears to have no views, and SchemaService reuses a cached row without ever asking how old it is
    /// — so a view would silently never reach the model until some unrelated policy change invalidated
    /// the cache by accident.
    ///
    /// Deleting the rows is safe by construction: SchemaCache is a cache, GetOrRefreshAsync repopulates
    /// on the next read, and the only cost is one live extraction per connection.
    ///
    /// The general alternative — a format-version column that GetOrRefreshAsync checks, so a stale-format
    /// row is rejected rather than deleted — is deliberately not built. One recurrence is not a pattern,
    /// and it would trade a migration nobody has to think about for a check on the hot read path forever.
    /// </summary>
    public partial class ClearSchemaCacheForViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM \"SchemaCaches\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo. The rows this dropped were a cache, and going back one migration does not
            // make the old JSON correct again — it makes it correct to regenerate, which the next read does.
        }
    }
}
