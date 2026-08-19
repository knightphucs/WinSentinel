using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskServiceMonitor.Migrations
{
    /// <inheritdoc />
    public partial class AddBlacklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Blacklist",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Value = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    LearnedFromRuleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LearnedFromObjectName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    HitCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastHitAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Blacklist", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Blacklist_Dedup",
                table: "Blacklist",
                columns: new[] { "Kind", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Blacklist_Enabled",
                table: "Blacklist",
                column: "Enabled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Blacklist");
        }
    }
}
