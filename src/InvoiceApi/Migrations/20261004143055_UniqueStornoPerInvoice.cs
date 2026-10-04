using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceApi.Migrations
{
    /// <inheritdoc />
    public partial class UniqueStornoPerInvoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_CancellationOfId",
                table: "Invoices");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_CancellationOfId",
                table: "Invoices",
                column: "CancellationOfId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_CancellationOfId",
                table: "Invoices");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_CancellationOfId",
                table: "Invoices",
                column: "CancellationOfId");
        }
    }
}
