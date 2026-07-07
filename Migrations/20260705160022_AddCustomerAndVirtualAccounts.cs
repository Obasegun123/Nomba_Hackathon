using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nomba_Hackathon.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerAndVirtualAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    phone_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    kyc_tier = table.Column<int>(type: "integer", nullable: false),
                    daily_limit = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "misdirected_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    received_account_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    intended_customer_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    reason = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolution_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_misdirected_payments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_exceptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    exception_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ai_diagnosis = table.Column<string>(type: "text", nullable: true),
                    ai_recommendation = table.Column<string>(type: "text", nullable: true),
                    ai_confidence = table.Column<decimal>(type: "numeric(3,2)", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    approved_by = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    resolution_action = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    context_data = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliation_exceptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    account_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transactions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "virtual_accounts",
                columns: table => new
                {
                    account_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    customer_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    account_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    kyc_tier = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_virtual_accounts", x => x.account_ref);
                    table.ForeignKey(
                        name: "FK_virtual_accounts_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ledgerentries",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    debit_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    credit_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    entry_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledgerentries", x => x.id);
                    table.ForeignKey(
                        name: "FK_ledgerentries_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_customers_email",
                table: "customers",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_customers_status",
                table: "customers",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_ledger_account",
                table: "ledgerentries",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_ledgerentries_transaction_id",
                table: "ledgerentries",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "idx_misdirected_account",
                table: "misdirected_payments",
                column: "received_account_ref");

            migrationBuilder.CreateIndex(
                name: "idx_misdirected_ref",
                table: "misdirected_payments",
                column: "transaction_ref");

            migrationBuilder.CreateIndex(
                name: "idx_misdirected_status",
                table: "misdirected_payments",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_exceptions_ref",
                table: "reconciliation_exceptions",
                column: "transaction_ref");

            migrationBuilder.CreateIndex(
                name: "idx_exceptions_status",
                table: "reconciliation_exceptions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_transactions_reference_code",
                table: "transactions",
                column: "reference_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_virtual_accounts_customer",
                table: "virtual_accounts",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "idx_virtual_accounts_status",
                table: "virtual_accounts",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ledgerentries");

            migrationBuilder.DropTable(
                name: "misdirected_payments");

            migrationBuilder.DropTable(
                name: "reconciliation_exceptions");

            migrationBuilder.DropTable(
                name: "virtual_accounts");

            migrationBuilder.DropTable(
                name: "transactions");

            migrationBuilder.DropTable(
                name: "customers");
        }
    }
}
