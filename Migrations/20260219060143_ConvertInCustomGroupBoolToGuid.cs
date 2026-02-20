using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessionApp.Migrations
{
    /// <inheritdoc />
    public partial class ConvertInCustomGroupBoolToGuid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "InCustomGroupNew",
                table: "Participants",
                type: "uuid",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.Sql(
                "UPDATE \"Participants\" SET \"InCustomGroupNew\" = '00000000-0000-0000-0000-000000000000'");

            migrationBuilder.DropColumn(
                name: "InCustomGroup",
                table: "Participants");

            migrationBuilder.RenameColumn(
                name: "InCustomGroupNew",
                table: "Participants",
                newName: "InCustomGroup");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
