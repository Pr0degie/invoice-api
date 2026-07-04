using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceApi.Migrations
{
    /// <inheritdoc />
    public partial class AddEInvoiceXRechnungSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "Users",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuyerReference",
                table: "Invoices",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientCity",
                table: "Invoices",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientCountryCode",
                table: "Invoices",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientEmail",
                table: "Invoices",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientPostalCode",
                table: "Invoices",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientStreet",
                table: "Invoices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientVatId",
                table: "Invoices",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InvoiceXmls",
                columns: table => new
                {
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceXmls", x => x.InvoiceId);
                    table.ForeignKey(
                        name: "FK_InvoiceXmls_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceXmls");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BuyerReference",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RecipientCity",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RecipientCountryCode",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RecipientEmail",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RecipientPostalCode",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RecipientStreet",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RecipientVatId",
                table: "Invoices");
        }
    }
}
