using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nomba_Hackathon.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogsForKycAndIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bank_code",
                table: "virtual_accounts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bank_name",
                table: "virtual_accounts",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "nuban",
                table: "virtual_accounts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "match_confidence",
                table: "transactions",
                type: "numeric(5,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "match_type",
                table: "transactions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payer_name",
                table: "transactions",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "payment_plan_id",
                table: "transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "identity_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    field_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    old_value = table.Column<string>(type: "text", nullable: true),
                    new_value = table.Column<string>(type: "text", nullable: true),
                    changed_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "kyc_tier_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    old_tier = table.Column<int>(type: "integer", nullable: false),
                    new_tier = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    changed_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kyc_tier_audit_logs", x => x.id);
                    table.ForeignKey(
                        name: "FK_kyc_tier_audit_logs_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "payment_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    account_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    total_expected = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    total_received = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    remaining_balance = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    installments_received = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_payment_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    account_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    customer_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    payload_json = table.Column<string>(type: "jsonb", nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    secret = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    last_delivery_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    success_count = table.Column<int>(type: "integer", nullable: false),
                    failure_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_subscriptions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_virtual_accounts_nuban",
                table: "virtual_accounts",
                column: "nuban");

            migrationBuilder.CreateIndex(
                name: "idx_identity_audit_entity",
                table: "identity_audit_logs",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "idx_identity_audit_timestamp",
                table: "identity_audit_logs",
                column: "changed_at");

            migrationBuilder.CreateIndex(
                name: "idx_identity_audit_type",
                table: "identity_audit_logs",
                column: "entity_type");

            migrationBuilder.CreateIndex(
                name: "idx_kyc_audit_customer",
                table: "kyc_tier_audit_logs",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "idx_kyc_audit_timestamp",
                table: "kyc_tier_audit_logs",
                column: "changed_at");

            migrationBuilder.CreateIndex(
                name: "idx_payment_plans_account",
                table: "payment_plans",
                column: "account_ref");

            migrationBuilder.CreateIndex(
                name: "idx_payment_plans_ref",
                table: "payment_plans",
                column: "transaction_ref");

            migrationBuilder.CreateIndex(
                name: "idx_payment_plans_status",
                table: "payment_plans",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_webhook_events_account",
                table: "webhook_events",
                column: "account_ref");

            migrationBuilder.CreateIndex(
                name: "idx_webhook_events_status",
                table: "webhook_events",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_webhook_subscriptions_status",
                table: "webhook_subscriptions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_webhook_subscriptions_url",
                table: "webhook_subscriptions",
                column: "url",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "identity_audit_logs");

            migrationBuilder.DropTable(
                name: "kyc_tier_audit_logs");

            migrationBuilder.DropTable(
                name: "payment_plans");

            migrationBuilder.DropTable(
                name: "webhook_events");

            migrationBuilder.DropTable(
                name: "webhook_subscriptions");

            migrationBuilder.DropIndex(
                name: "idx_virtual_accounts_nuban",
                table: "virtual_accounts");

            migrationBuilder.DropColumn(
                name: "bank_code",
                table: "virtual_accounts");

            migrationBuilder.DropColumn(
                name: "bank_name",
                table: "virtual_accounts");

            migrationBuilder.DropColumn(
                name: "nuban",
                table: "virtual_accounts");

            migrationBuilder.DropColumn(
                name: "match_confidence",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "match_type",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "payer_name",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "payment_plan_id",
                table: "transactions");
        }
    }
}
