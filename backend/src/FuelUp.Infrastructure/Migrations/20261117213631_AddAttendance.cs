using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FuelUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attendance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    check_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    check_out_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    in_lat = table.Column<double>(type: "double precision", nullable: true),
                    in_lng = table.Column<double>(type: "double precision", nullable: true),
                    out_lat = table.Column<double>(type: "double precision", nullable: true),
                    out_lng = table.Column<double>(type: "double precision", nullable: true),
                    in_ip = table.Column<string>(type: "text", nullable: true),
                    out_ip = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    corrected = table.Column<bool>(type: "boolean", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attendance", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_company_id_day",
                table: "attendance",
                columns: new[] { "company_id", "day" });

            migrationBuilder.CreateIndex(
                name: "ux_attendance_open",
                table: "attendance",
                column: "user_id",
                unique: true,
                filter: "check_out_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attendance");
        }
    }
}
