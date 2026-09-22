using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlAgent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AllowedDdl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AllowedDdl",
                table: "DatabaseConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedDdl",
                table: "DatabaseConnections");
        }
    }
}
