using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlAgent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SchemaDiagramOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SchemaDiagramConnectionId",
                table: "ChatMessages",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SchemaDiagramConnectionId",
                table: "ChatMessages");
        }
    }
}
