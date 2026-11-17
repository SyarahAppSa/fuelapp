using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FuelUp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustody : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_parts_company_id_brand_serial",
                table: "parts");

            migrationBuilder.AddColumn<string>(
                name: "close_note",
                table: "work_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "closed_at",
                table: "work_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "closed_by",
                table: "work_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "driver_id",
                table: "work_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "km",
                table: "work_orders",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "opened_by",
                table: "work_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "workshop",
                table: "work_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "warehouses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "keeper",
                table: "warehouses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "location",
                table: "warehouses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bin",
                table: "stock_balances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "batch_no",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bin",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claim_opened_at",
                table: "parts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "claim_ref",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "claim_result",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "condition",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "parts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateOnly>(
                name: "expiry_date",
                table: "parts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "holder_id",
                table: "parts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "holder_kind",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "holder_since",
                table: "parts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "install_hours",
                table: "parts",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "last_inspection",
                table: "parts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_location",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "meter_id",
                table: "parts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "next_inspection",
                table: "parts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "notes",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "repair_cost",
                table: "parts",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "repair_expected_at",
                table: "parts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "repair_sent_at",
                table: "parts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repair_vendor",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_outcome",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "scrap_approved_by",
                table: "parts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "serial_key",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "specs",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status_reason",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "warranty_start",
                table: "parts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "warranty_terms",
                table: "parts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "approved_by",
                table: "part_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "batch_no",
                table: "part_movements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "corrects_id",
                table: "part_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "cost",
                table: "part_movements",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "doc_ref",
                table: "part_movements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "expected_return",
                table: "part_movements",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "from_holder_id",
                table: "part_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "from_holder_kind",
                table: "part_movements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "from_holder_ref",
                table: "part_movements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "from_vehicle_id",
                table: "part_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "from_warehouse_id",
                table: "part_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "holder_id",
                table: "part_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "holder_kind",
                table: "part_movements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "hours",
                table: "part_movements",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "part_movements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "position",
                table: "part_movements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "related_part_id",
                table: "part_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status_after",
                table: "part_movements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status_before",
                table: "part_movements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "to_warehouse_id",
                table: "part_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "transfer_id",
                table: "part_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "unit_cost",
                table: "part_movements",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vendor",
                table: "part_movements",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "part_catalog",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "specs",
                table: "part_catalog",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "unit_cost",
                table: "part_catalog",
                type: "numeric(14,2)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "employees",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "phone",
                table: "employees",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "custody_transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    no = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    part_id = table.Column<Guid>(type: "uuid", nullable: true),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    qty = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    from_kind = table.Column<string>(type: "text", nullable: false),
                    from_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_name = table.Column<string>(type: "text", nullable: false),
                    to_kind = table.Column<string>(type: "text", nullable: false),
                    to_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_name = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<string>(type: "text", nullable: true),
                    km = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    condition = table.Column<string>(type: "text", nullable: true),
                    handover_by = table.Column<string>(type: "text", nullable: true),
                    handover_note = table.Column<string>(type: "text", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decision_note = table.Column<string>(type: "text", nullable: true),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_custody_transfers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "holder_balances",
                columns: table => new
                {
                    holder_kind = table.Column<string>(type: "text", nullable: false),
                    holder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    qty = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    holder_name = table.Column<string>(type: "text", nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_holder_balances", x => new { x.holder_kind, x.holder_id, x.catalog_id });
                    table.CheckConstraint("ck_holder_balances_qty", "qty >= 0");
                });

            migrationBuilder.CreateTable(
                name: "stock_counts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    no = table.Column<string>(type: "text", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    missing_parts = table.Column<string>(type: "jsonb", nullable: false),
                    unexpected_parts = table.Column<string>(type: "jsonb", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_counts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_vehicle_id",
                table: "work_orders",
                column: "vehicle_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_balances_qty",
                table: "stock_balances",
                sql: "qty >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_parts_company_id_status",
                table: "parts",
                columns: new[] { "company_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_parts_holder_kind_holder_id",
                table: "parts",
                columns: new[] { "holder_kind", "holder_id" });

            migrationBuilder.CreateIndex(
                name: "ix_parts_serial",
                table: "parts",
                column: "serial");

            migrationBuilder.CreateIndex(
                name: "ux_parts_serial_key",
                table: "parts",
                columns: new[] { "company_id", "serial_key" },
                unique: true,
                filter: "serial_key IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_parts_installed_vehicle",
                table: "parts",
                sql: "(status = 'Installed') = (vehicle_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_part_movements_company_id_type_at",
                table: "part_movements",
                columns: new[] { "company_id", "type", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_part_movements_vehicle_id_at",
                table: "part_movements",
                columns: new[] { "vehicle_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_part_movements_work_order_id",
                table: "part_movements",
                column: "work_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_custody_transfers_company_id_no",
                table: "custody_transfers",
                columns: new[] { "company_id", "no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_custody_transfers_to_kind_to_id_status",
                table: "custody_transfers",
                columns: new[] { "to_kind", "to_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_custody_transfers_pending_part",
                table: "custody_transfers",
                column: "part_id",
                unique: true,
                filter: "status = 'Pending' AND part_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_stock_counts_company_id_no",
                table: "stock_counts",
                columns: new[] { "company_id", "no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custody_transfers");

            migrationBuilder.DropTable(
                name: "holder_balances");

            migrationBuilder.DropTable(
                name: "stock_counts");

            migrationBuilder.DropIndex(
                name: "ix_work_orders_vehicle_id",
                table: "work_orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_balances_qty",
                table: "stock_balances");

            migrationBuilder.DropIndex(
                name: "ix_parts_company_id_status",
                table: "parts");

            migrationBuilder.DropIndex(
                name: "ix_parts_holder_kind_holder_id",
                table: "parts");

            migrationBuilder.DropIndex(
                name: "ix_parts_serial",
                table: "parts");

            migrationBuilder.DropIndex(
                name: "ux_parts_serial_key",
                table: "parts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_parts_installed_vehicle",
                table: "parts");

            migrationBuilder.DropIndex(
                name: "ix_part_movements_company_id_type_at",
                table: "part_movements");

            migrationBuilder.DropIndex(
                name: "ix_part_movements_vehicle_id_at",
                table: "part_movements");

            migrationBuilder.DropIndex(
                name: "ix_part_movements_work_order_id",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "close_note",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "closed_at",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "closed_by",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "driver_id",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "km",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "opened_by",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "workshop",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "keeper",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "location",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "bin",
                table: "stock_balances");

            migrationBuilder.DropColumn(
                name: "batch_no",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "bin",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "claim_opened_at",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "claim_ref",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "claim_result",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "condition",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "expiry_date",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "holder_id",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "holder_kind",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "holder_since",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "install_hours",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "last_inspection",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "last_location",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "meter_id",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "next_inspection",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "notes",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "repair_cost",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "repair_expected_at",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "repair_sent_at",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "repair_vendor",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "review_outcome",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "scrap_approved_by",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "serial_key",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "specs",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "status_reason",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "warranty_start",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "warranty_terms",
                table: "parts");

            migrationBuilder.DropColumn(
                name: "approved_by",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "batch_no",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "corrects_id",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "cost",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "doc_ref",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "expected_return",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "from_holder_id",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "from_holder_kind",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "from_holder_ref",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "from_vehicle_id",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "from_warehouse_id",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "holder_id",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "holder_kind",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "hours",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "note",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "position",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "related_part_id",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "status_after",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "status_before",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "to_warehouse_id",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "transfer_id",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "unit_cost",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "vendor",
                table: "part_movements");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "part_catalog");

            migrationBuilder.DropColumn(
                name: "specs",
                table: "part_catalog");

            migrationBuilder.DropColumn(
                name: "unit_cost",
                table: "part_catalog");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "phone",
                table: "employees");

            migrationBuilder.CreateIndex(
                name: "ix_parts_company_id_brand_serial",
                table: "parts",
                columns: new[] { "company_id", "brand", "serial" },
                unique: true,
                filter: "serial IS NOT NULL");
        }
    }
}
