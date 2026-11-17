using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FuelUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFillReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trailer_plate",
                table: "vehicles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "national_id",
                table: "drivers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "client_id",
                table: "attachments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "location_note",
                table: "attachments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fill_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    no = table.Column<string>(type: "text", nullable: false),
                    trip_id = table.Column<Guid>(type: "uuid", nullable: false),
                    driver_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_by = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trip_no = table.Column<string>(type: "text", nullable: false),
                    plate = table.Column<string>(type: "text", nullable: false),
                    trailer_plate = table.Column<string>(type: "text", nullable: true),
                    driver_name = table.Column<string>(type: "text", nullable: false),
                    customer_name = table.Column<string>(type: "text", nullable: false),
                    delivery_site = table.Column<string>(type: "text", nullable: false),
                    loading_site = table.Column<string>(type: "text", nullable: false),
                    product = table.Column<string>(type: "text", nullable: false),
                    qty_requested = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    pre_fill_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    post_fill_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    loaded_qty = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    loaded_from_trip = table.Column<bool>(type: "boolean", nullable: false),
                    arrived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    secret_meter_serial = table.Column<string>(type: "text", nullable: true),
                    meter_before = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    unloaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    meter_after = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    outcome_reason = table.Column<string>(type: "text", nullable: true),
                    tank_state = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_reasons = table.Column<string>(type: "text", nullable: true),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_note = table.Column<string>(type: "text", nullable: true),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fill_reports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_owner_id_client_id",
                table: "attachments",
                columns: new[] { "owner_id", "client_id" },
                unique: true,
                filter: "client_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_fill_reports_company_id_no",
                table: "fill_reports",
                columns: new[] { "company_id", "no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fill_reports_company_id_status_started_at",
                table: "fill_reports",
                columns: new[] { "company_id", "status", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_fill_reports_trip_id",
                table: "fill_reports",
                column: "trip_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fill_reports");

            migrationBuilder.DropIndex(
                name: "ix_attachments_owner_id_client_id",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "trailer_plate",
                table: "vehicles");

            migrationBuilder.DropColumn(
                name: "national_id",
                table: "drivers");

            migrationBuilder.DropColumn(
                name: "client_id",
                table: "attachments");

            migrationBuilder.DropColumn(
                name: "location_note",
                table: "attachments");
        }
    }
}
