using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceCommandTransport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PresetCode",
                table: "alert_commands",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);

            migrationBuilder.AddColumn<int>(
                name: "MaximumPlaybackDurationSeconds",
                table: "alert_commands",
                type: "integer",
                nullable: false,
                defaultValue: 900);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetCommandId",
                table: "alert_commands",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_commands_TargetCommandId",
                table: "alert_commands",
                column: "TargetCommandId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_alert_commands_device_payload",
                table: "alert_commands",
                sql: "(\"Action\" = 'start' AND \"PresetCode\" IS NOT NULL AND \"TargetCommandId\" IS NULL AND \"MaximumPlaybackDurationSeconds\" BETWEEN 1 AND 900) OR (\"Action\" = 'stop' AND \"PresetCode\" IS NULL AND \"TargetCommandId\" IS NOT NULL AND \"MaximumPlaybackDurationSeconds\" = 0)");

            migrationBuilder.AddForeignKey(
                name: "FK_alert_commands_alert_commands_TargetCommandId",
                table: "alert_commands",
                column: "TargetCommandId",
                principalTable: "alert_commands",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM alert_commands
                        WHERE "Action" <> 'start'
                           OR "MaximumPlaybackDurationSeconds" <> 900
                           OR "TargetCommandId" IS NOT NULL
                    ) THEN
                        RAISE EXCEPTION 'Відкат неможливий, поки існують команди з новим контрактом пристроїв.'
                            USING ERRCODE = '55000';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_alert_commands_alert_commands_TargetCommandId",
                table: "alert_commands");

            migrationBuilder.DropIndex(
                name: "IX_alert_commands_TargetCommandId",
                table: "alert_commands");

            migrationBuilder.DropCheckConstraint(
                name: "CK_alert_commands_device_payload",
                table: "alert_commands");

            migrationBuilder.DropColumn(
                name: "MaximumPlaybackDurationSeconds",
                table: "alert_commands");

            migrationBuilder.DropColumn(
                name: "TargetCommandId",
                table: "alert_commands");

            migrationBuilder.AlterColumn<string>(
                name: "PresetCode",
                table: "alert_commands",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80,
                oldNullable: true);
        }
    }
}
