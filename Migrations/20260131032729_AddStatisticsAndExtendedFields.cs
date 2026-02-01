using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessionApp.Migrations
{
    /// <inheritdoc />
    public partial class AddStatisticsAndExtendedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArchivedRounds_SessionCode",
                table: "ArchivedRounds");

            migrationBuilder.AddColumn<bool>(
                name: "Archived",
                table: "Sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EventName",
                table: "Sessions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Commander",
                table: "Participants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Points",
                table: "Participants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAtUtc",
                table: "Groups",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAtUtc",
                table: "Groups",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatisticsJson",
                table: "Groups",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "Commander",
                table: "ArchivedRounds",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAtUtc",
                table: "ArchivedRounds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatisticsJson",
                table: "ArchivedRounds",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<int>(
                name: "TurnCount",
                table: "ArchivedRounds",
                type: "integer",
                nullable: false,
                defaultValue: -1);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_Archived",
                table: "Sessions",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_Groups_RoundNumber",
                table: "Groups",
                column: "RoundNumber");

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedRounds_SessionCode_RoundNumber",
                table: "ArchivedRounds",
                columns: new[] { "SessionCode", "RoundNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_Archived",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Groups_RoundNumber",
                table: "Groups");

            migrationBuilder.DropIndex(
                name: "IX_ArchivedRounds_SessionCode_RoundNumber",
                table: "ArchivedRounds");

            migrationBuilder.DropColumn(
                name: "Archived",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "EventName",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "Commander",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "Points",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "StartedAtUtc",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "StatisticsJson",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "Commander",
                table: "ArchivedRounds");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "ArchivedRounds");

            migrationBuilder.DropColumn(
                name: "StatisticsJson",
                table: "ArchivedRounds");

            migrationBuilder.DropColumn(
                name: "TurnCount",
                table: "ArchivedRounds");

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedRounds_SessionCode",
                table: "ArchivedRounds",
                column: "SessionCode");
        }
    }
}
