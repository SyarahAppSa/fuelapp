using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FuelUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrdersAndDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "order_id",
                table: "trips",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "customer_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    no = table.Column<string>(type: "text", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    product = table.Column<string>(type: "text", nullable: false),
                    qty_requested = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    priority = table.Column<string>(type: "text", nullable: false),
                    requested_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requested_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    customer_ref = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    transport_rate_per_liter = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    fuel_price_per_liter = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    status_reason = table.Column<string>(type: "text", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_name = table.Column<string>(type: "text", nullable: false),
                    site = table.Column<string>(type: "text", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expiry = table.Column<DateOnly>(type: "date", nullable: false),
                    offset_days = table.Column<int>(type: "integer", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recipients = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_alerts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    applies_to = table.Column<string>(type: "text", nullable: false),
                    fields = table.Column<string>(type: "jsonb", nullable: false),
                    mandatory_for_dispatch = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fleet_document_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    number = table.Column<string>(type: "text", nullable: true),
                    category = table.Column<string>(type: "text", nullable: true),
                    issuer = table.Column<string>(type: "text", nullable: true),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    expiry = table.Column<DateOnly>(type: "date", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fleet_document_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fleet_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_kind = table.Column<string>(type: "text", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "text", nullable: true),
                    category = table.Column<string>(type: "text", nullable: true),
                    issuer = table.Column<string>(type: "text", nullable: true),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    expiry = table.Column<DateOnly>(type: "date", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fleet_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "order_counters",
                columns: table => new
                {
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    last = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_counters", x => new { x.company_id, x.period });
                });

            migrationBuilder.CreateIndex(
                name: "ix_trips_order_id",
                table: "trips",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_orders_company_id_no",
                table: "customer_orders",
                columns: new[] { "company_id", "no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customer_orders_company_id_status_created_at",
                table: "customer_orders",
                columns: new[] { "company_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_orders_customer_id",
                table: "customer_orders",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_alerts_document_id_expiry_offset_days",
                table: "document_alerts",
                columns: new[] { "document_id", "expiry", "offset_days" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_types_company_id_name",
                table: "document_types",
                columns: new[] { "company_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fleet_document_versions_document_id_version",
                table: "fleet_document_versions",
                columns: new[] { "document_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fleet_documents_company_id_expiry",
                table: "fleet_documents",
                columns: new[] { "company_id", "expiry" });

            migrationBuilder.CreateIndex(
                name: "ix_fleet_documents_company_id_type_id_owner_id",
                table: "fleet_documents",
                columns: new[] { "company_id", "type_id", "owner_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fleet_documents_owner_id",
                table: "fleet_documents",
                column: "owner_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_orders");

            migrationBuilder.DropTable(
                name: "document_alerts");

            migrationBuilder.DropTable(
                name: "document_types");

            migrationBuilder.DropTable(
                name: "fleet_document_versions");

            migrationBuilder.DropTable(
                name: "fleet_documents");

            migrationBuilder.DropTable(
                name: "order_counters");

            migrationBuilder.DropIndex(
                name: "ix_trips_order_id",
                table: "trips");

            migrationBuilder.DropColumn(
                name: "order_id",
                table: "trips");
        }
    }
}
