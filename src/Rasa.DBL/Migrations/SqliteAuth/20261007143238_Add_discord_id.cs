using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rasa.Migrations.SqliteAuth
{
    /// <inheritdoc />
    public partial class Add_discord_id : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_id",
                table: "account",
                type: "varchar(20)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "discord_id_UNIQUE",
                table: "account",
                column: "discord_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "discord_id_UNIQUE",
                table: "account");

            migrationBuilder.DropColumn(
                name: "discord_id",
                table: "account");
        }
    }
}
