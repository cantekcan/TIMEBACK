using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Timeback.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiModelToGames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiModel",
                table: "games",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiModel",
                table: "games");
        }
    }
}
