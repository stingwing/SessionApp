using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessionApp.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderDroppedInCustomGroupFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Dropped",
                table: "Participants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "InCustomGroup",
                table: "Participants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Order",
                table: "GroupParticipants",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Dropped",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "InCustomGroup",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "Order",
                table: "GroupParticipants");
        }
    }
}
