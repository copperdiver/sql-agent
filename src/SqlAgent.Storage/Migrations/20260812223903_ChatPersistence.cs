using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlAgent.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ChatPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Chats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    GeneratedSql = table.Column<string>(type: "TEXT", nullable: true),
                    OutcomeKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    RowCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ElapsedMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Truncated = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMessages_Chats_ChatId",
                        column: x => x.ChatId,
                        principalTable: "Chats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessageDatabases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatMessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DatabaseConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DatabaseName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessageDatabases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMessageDatabases_ChatMessages_ChatMessageId",
                        column: x => x.ChatMessageId,
                        principalTable: "ChatMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageDatabases_ChatMessageId",
                table: "ChatMessageDatabases",
                column: "ChatMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessageDatabases_ChatMessageId_DatabaseName",
                table: "ChatMessageDatabases",
                columns: new[] { "ChatMessageId", "DatabaseName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ChatId_Sequence",
                table: "ChatMessages",
                columns: new[] { "ChatId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Chats_LastMessageAt",
                table: "Chats",
                column: "LastMessageAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMessageDatabases");

            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "Chats");
        }
    }
}
