using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceApi.Migrations
{
    /// <inheritdoc />
    public partial class GermanInvoiceCompliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BankName",
                table: "Users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Bic",
                table: "Users",
                type: "character varying(11)",
                maxLength: 11,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "Users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "Users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Iban",
                table: "Users",
                type: "character varying(34)",
                maxLength: 34,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSmallBusiness",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                table: "Users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Street",
                table: "Users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxNumber",
                table: "Users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VatId",
                table: "Users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Number",
                table: "Invoices",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AddColumn<Guid>(
                name: "CancellationOfId",
                table: "Invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancellationOfNumber",
                table: "Invoices",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSmallBusiness",
                table: "Invoices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServiceDate",
                table: "Invoices",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServicePeriodEnd",
                table: "Invoices",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServicePeriodStart",
                table: "Invoices",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "Invoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "InvoiceNumberSequences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Counter = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceNumberSequences", x => new { x.UserId, x.Year });
                });

            migrationBuilder.CreateTable(
                name: "InvoicePdfs",
                columns: table => new
                {
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoicePdfs", x => x.InvoiceId);
                    table.ForeignKey(
                        name: "FK_InvoicePdfs_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_CancellationOfId",
                table: "Invoices",
                column: "CancellationOfId");

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Invoices_CancellationOfId",
                table: "Invoices",
                column: "CancellationOfId",
                principalTable: "Invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // ── Data migration ───────────────────────────────────────────────
            // Existing users created their invoices with 19% VAT — keep them on
            // Regelbesteuerung; the IsSmallBusiness=true default is for new signups.
            migrationBuilder.Sql("""UPDATE "Users" SET "IsSmallBusiness" = false;""");

            // Drafts no longer carry a number (assigned at finalization).
            migrationBuilder.Sql("""UPDATE "Invoices" SET "Number" = NULL WHERE "Status" = 0;""");

            // Status enum remap (stored as int):
            //   old: Draft=0, Sent=1, Paid=2, Overdue=3, Cancelled=4
            //   new: Draft=0, Finalized=1, Paid=2, Cancelled=3
            // Sent(1) → Finalized(1) needs no update. Overdue folds into Finalized
            // (it is derived from the due date now), then Cancelled shifts 4 → 3.
            migrationBuilder.Sql("""UPDATE "Invoices" SET "Status" = 1 WHERE "Status" = 3;""");
            migrationBuilder.Sql("""UPDATE "Invoices" SET "Status" = 3 WHERE "Status" = 4;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Invoices_CancellationOfId",
                table: "Invoices");

            migrationBuilder.DropTable(
                name: "InvoiceNumberSequences");

            migrationBuilder.DropTable(
                name: "InvoicePdfs");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_CancellationOfId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "BankName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Bic",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "City",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Iban",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsSmallBusiness",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Street",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TaxNumber",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "VatId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CancellationOfId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "CancellationOfNumber",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "IsSmallBusiness",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ServiceDate",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ServicePeriodEnd",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ServicePeriodStart",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Invoices");

            migrationBuilder.AlterColumn<string>(
                name: "Number",
                table: "Invoices",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50,
                oldNullable: true);
        }
    }
}
