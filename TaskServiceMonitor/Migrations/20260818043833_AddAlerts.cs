using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskServiceMonitor.Migrations
{
    /// <inheritdoc />
    public partial class AddAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    RuleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RuleName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ObjectType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ObjectName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    EventId = table.Column<int>(type: "integer", nullable: true),
                    Evidence = table.Column<string>(type: "text", nullable: false),
                    Recommendation = table.Column<string>(type: "text", nullable: true),
                    Acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceConfigSnapshots",
                columns: table => new
                {
                    Hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ServiceName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ImagePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Account = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    StartType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceConfigSnapshots", x => new { x.Hostname, x.ServiceName });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Acknowledged",
                table: "Alerts",
                column: "Acknowledged");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Dedup",
                table: "Alerts",
                columns: new[] { "SourceEventId", "RuleId" },
                unique: true,
                filter: "\"SourceEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_DetectedAt",
                table: "Alerts",
                column: "DetectedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_RuleId",
                table: "Alerts",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Severity",
                table: "Alerts",
                column: "Severity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "ServiceConfigSnapshots");
        }
    }
}
