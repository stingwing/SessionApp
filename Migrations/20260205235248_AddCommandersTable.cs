using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessionApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandersTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Commanders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ScryfallUri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    LegalitiesJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    LastUpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commanders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Commanders_LastUpdatedUtc",
                table: "Commanders",
                column: "LastUpdatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Commanders_Name",
                table: "Commanders",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Commanders");
        }
    }
}
