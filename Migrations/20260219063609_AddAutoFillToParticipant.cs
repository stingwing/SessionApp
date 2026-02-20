using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessionApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoFillToParticipant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoFill",
                table: "GroupParticipants",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoFill",
                table: "GroupParticipants");
        }
    }
}
