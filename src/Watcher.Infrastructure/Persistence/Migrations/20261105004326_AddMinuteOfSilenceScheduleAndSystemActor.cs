using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMinuteOfSilenceScheduleAndSystemActor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "InitiatedByUserId",
                table: "alert_commands",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "minute_of_silence_occurrences",
                columns: table => new
                {
                    run_date = table.Column<DateOnly>(type: "date", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SkipReason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_minute_of_silence_occurrences", x => x.run_date);
                    table.ForeignKey(
                        name: "FK_minute_of_silence_occurrences_alert_commands_CommandId",
                        column: x => x.CommandId,
                        principalTable: "alert_commands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_minute_of_silence_occurrences_CommandId",
                table: "minute_of_silence_occurrences",
                column: "CommandId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "minute_of_silence_occurrences");

            migrationBuilder.Sql(
                "UPDATE alert_commands SET \"InitiatedByUserId\" = '00000000-0000-0000-0000-000000000000' " +
                "WHERE \"InitiatedByUserId\" IS NULL");

            migrationBuilder.AlterColumn<Guid>(
                name: "InitiatedByUserId",
                table: "alert_commands",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
