using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskServiceMonitor.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SUA TAY: EF sinh ra defaultValue "" (chuoi rong) cho cot string non-null.
            // Chuoi rong KHONG phai ten hop le cua enum RiskLevel -> doc nguoc tu DB
            // se nem loi parse tren toan bo dong cu. Dat "Low" lam mac dinh, sau do
            // chay `dotnet run --project TaskServiceMonitor -- --rescore` de cham lai
            // dung muc cho cac dong da co.
            migrationBuilder.AddColumn<string>(
                name: "RiskLevel",
                table: "Events",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Low");

            migrationBuilder.CreateIndex(
                name: "IX_Events_RiskLevel",
                table: "Events",
                column: "RiskLevel");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Events_RiskLevel",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "RiskLevel",
                table: "Events");
        }
    }
}
