using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskServiceMonitor.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskSchedulerOperationalFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TaskActionResultCode",
                table: "Events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaskInstanceId",
                table: "Events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TaskActionResultCode",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "TaskInstanceId",
                table: "Events");
        }
    }
}
