using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticAlertTransitionTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_alert_commands_device_payload",
                table: "alert_commands");

            migrationBuilder.AddColumn<int>(
                name: "CommandSchemaVersion",
                table: "device_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "HasObservationBaseline",
                table: "automatic_alert_dispatch_states",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastAttemptedAlertLevel",
                table: "automatic_alert_dispatch_states",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastAttemptedAlertLevelScope",
                table: "automatic_alert_dispatch_states",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastAttemptedAlertLevelScopeId",
                table: "automatic_alert_dispatch_states",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastObservedAlertLevel",
                table: "automatic_alert_dispatch_states",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastObservedLevel",
                table: "automatic_alert_dispatch_states",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastObservedScope",
                table: "automatic_alert_dispatch_states",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastObservedScopeId",
                table: "automatic_alert_dispatch_states",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastObservedSource",
                table: "automatic_alert_dispatch_states",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsObservationResync",
                table: "automatic_alert_dispatch_states",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_device_sessions_command_schema_version",
                table: "device_sessions",
                sql: "\"CommandSchemaVersion\" IN (1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_alert_commands_device_payload",
                table: "alert_commands",
                sql: "(\"Action\" = 'start' AND \"PresetCode\" IS NOT NULL AND \"TargetCommandId\" IS NULL AND \"MaximumPlaybackDurationSeconds\" BETWEEN 1 AND 900) OR (\"Action\" = 'replace' AND \"PresetCode\" IS NOT NULL AND \"TargetCommandId\" IS NOT NULL AND \"MaximumPlaybackDurationSeconds\" BETWEEN 1 AND 900) OR (\"Action\" = 'stop' AND \"PresetCode\" IS NULL AND \"TargetCommandId\" IS NOT NULL AND \"MaximumPlaybackDurationSeconds\" = 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_device_sessions_command_schema_version",
                table: "device_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_alert_commands_device_payload",
                table: "alert_commands");

            migrationBuilder.DropColumn(
                name: "CommandSchemaVersion",
                table: "device_sessions");

            migrationBuilder.DropColumn(
                name: "HasObservationBaseline",
                table: "automatic_alert_dispatch_states");

            migrationBuilder.DropColumn(
                name: "LastAttemptedAlertLevel",
                table: "automatic_alert_dispatch_states");

            migrationBuilder.DropColumn(
                name: "LastAttemptedAlertLevelScope",
                table: "automatic_alert_dispatch_states");

            migrationBuilder.DropColumn(
                name: "LastAttemptedAlertLevelScopeId",
                table: "automatic_alert_dispatch_states");

            migrationBuilder.DropColumn(
                name: "LastObservedAlertLevel",
                table: "automatic_alert_dispatch_states");

            migrationBuilder.DropColumn(
                name: "LastObservedLevel",
                table: "automatic_alert_dispatch_states");

            migrationBuilder.DropColumn(
                name: "LastObservedScope",
                table: "automatic_alert_dispatch_states");

            migrationBuilder.DropColumn(
                name: "LastObservedScopeId",
                table: "automatic_alert_dispatch_states");

            migrationBuilder.DropColumn(
                name: "LastObservedSource",
                table: "automatic_alert_dispatch_states");

            migrationBuilder.DropColumn(
                name: "NeedsObservationResync",
                table: "automatic_alert_dispatch_states");

            migrationBuilder.AddCheckConstraint(
                name: "CK_alert_commands_device_payload",
                table: "alert_commands",
                sql: "(\"Action\" = 'start' AND \"PresetCode\" IS NOT NULL AND \"TargetCommandId\" IS NULL AND \"MaximumPlaybackDurationSeconds\" BETWEEN 1 AND 900) OR (\"Action\" = 'stop' AND \"PresetCode\" IS NULL AND \"TargetCommandId\" IS NOT NULL AND \"MaximumPlaybackDurationSeconds\" = 0)");
        }
    }
}
