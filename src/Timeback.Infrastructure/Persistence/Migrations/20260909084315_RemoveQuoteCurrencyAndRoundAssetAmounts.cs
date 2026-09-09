using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Timeback.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveQuoteCurrencyAndRoundAssetAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuoteCurrency",
                table: "assets");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QuoteCurrency",
                table: "assets",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");
        }
    }
}
