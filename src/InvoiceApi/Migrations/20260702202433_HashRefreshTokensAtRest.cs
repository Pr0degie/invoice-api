using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceApi.Migrations
{
    /// <inheritdoc />
    public partial class HashRefreshTokensAtRest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tokens are now stored as SHA-256 hashes. Existing raw-token rows
            // can't be converted server-side — delete them; users re-login.
            migrationBuilder.Sql("DELETE FROM \"RefreshTokens\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
