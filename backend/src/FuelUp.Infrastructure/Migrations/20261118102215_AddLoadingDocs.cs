using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FuelUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadingDocs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "loading_doc_no",
                table: "fill_reports",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "retake_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_attachment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retake_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_retake_requests_report_id",
                table: "retake_requests",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "ux_retake_requests_open",
                table: "retake_requests",
                columns: new[] { "report_id", "kind" },
                unique: true,
                filter: "resolved_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "retake_requests");

            migrationBuilder.DropColumn(
                name: "loading_doc_no",
                table: "fill_reports");
        }
    }
}
