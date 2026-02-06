using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessionApp.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    EventName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    HostId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsGameStarted = table.Column<bool>(type: "boolean", nullable: false),
                    IsGameEnded = table.Column<bool>(type: "boolean", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CurrentRound = table.Column<int>(type: "integer", nullable: false),
                    WinnerParticipantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SettingsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "ArchivedRounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    RoundNumber = table.Column<int>(type: "integer", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Commander = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    TurnCount = table.Column<int>(type: "integer", nullable: false, defaultValue: -1),
                    StatisticsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchivedRounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArchivedRounds_Sessions_SessionCode",
                        column: x => x.SessionCode,
                        principalTable: "Sessions",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Participants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Commander = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    Points = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Participants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Participants_Sessions_SessionCode",
                        column: x => x.SessionCode,
                        principalTable: "Sessions",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    GroupNumber = table.Column<int>(type: "integer", nullable: false),
                    RoundNumber = table.Column<int>(type: "integer", nullable: false),
                    IsDraw = table.Column<bool>(type: "boolean", nullable: false),
                    HasResult = table.Column<bool>(type: "boolean", nullable: false),
                    WinnerParticipantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    ArchivedRoundId = table.Column<Guid>(type: "uuid", nullable: true),
                    RoundStarted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StatisticsJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Groups_ArchivedRounds_ArchivedRoundId",
                        column: x => x.ArchivedRoundId,
                        principalTable: "ArchivedRounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Groups_Sessions_SessionCode",
                        column: x => x.SessionCode,
                        principalTable: "Sessions",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupParticipants_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedRounds_SessionCode_RoundNumber",
                table: "ArchivedRounds",
                columns: new[] { "SessionCode", "RoundNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupParticipants_GroupId",
                table: "GroupParticipants",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Groups_ArchivedRoundId",
                table: "Groups",
                column: "ArchivedRoundId");

            migrationBuilder.CreateIndex(
                name: "IX_Groups_RoundNumber",
                table: "Groups",
                column: "RoundNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Groups_SessionCode",
                table: "Groups",
                column: "SessionCode");

            migrationBuilder.CreateIndex(
                name: "IX_Participants_SessionCode_ParticipantId",
                table: "Participants",
                columns: new[] { "SessionCode", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_Archived",
                table: "Sessions",
                column: "Archived");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ExpiresAtUtc",
                table: "Sessions",
                column: "ExpiresAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupParticipants");

            migrationBuilder.DropTable(
                name: "Participants");

            migrationBuilder.DropTable(
                name: "Groups");

            migrationBuilder.DropTable(
                name: "ArchivedRounds");

            migrationBuilder.DropTable(
                name: "Sessions");
        }
    }
}
