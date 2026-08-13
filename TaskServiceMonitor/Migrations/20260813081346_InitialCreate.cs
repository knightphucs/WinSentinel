using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskServiceMonitor.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    Hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TimeCreated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorAccount = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ObjectType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ObjectName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ActionDescription = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RawXml = table.Column<string>(type: "text", nullable: false),
                    Channel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RecordId = table.Column<long>(type: "bigint", nullable: true),
                    ActorSid = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ImagePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ServiceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StartType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousStartType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ServiceAccount = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TaskActionType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TaskComHandlerClassId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TaskCommand = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    TaskArguments = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    TaskRunAsUser = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TaskRunLevel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TaskContentXml = table.Column<string>(type: "text", nullable: true),
                    IsRecognized = table.Column<bool>(type: "boolean", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_Dedup",
                table: "Events",
                columns: new[] { "Hostname", "Channel", "RecordId" },
                unique: true,
                filter: "\"RecordId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Events_Hostname",
                table: "Events",
                column: "Hostname");

            migrationBuilder.CreateIndex(
                name: "IX_Events_ObjectType",
                table: "Events",
                column: "ObjectType");

            migrationBuilder.CreateIndex(
                name: "IX_Events_TimeCreated",
                table: "Events",
                column: "TimeCreated",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
