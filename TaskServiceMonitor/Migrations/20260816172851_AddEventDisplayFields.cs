using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskServiceMonitor.Migrations
{
    /// <inheritdoc />
    public partial class AddEventDisplayFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Keywords",
                table: "Events",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Level",
                table: "Events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LevelDisplayName",
                table: "Events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpcodeName",
                table: "Events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TaskCategoryId",
                table: "Events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaskCategoryName",
                table: "Events",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "Keywords",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "Level",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "LevelDisplayName",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "OpcodeName",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "TaskCategoryId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "TaskCategoryName",
                table: "Events");
        }
    }
}
