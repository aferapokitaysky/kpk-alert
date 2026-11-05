using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "command_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_command_deliveries", x => x.Id);
                    table.CheckConstraint("CK_command_deliveries_attempt_count", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_command_deliveries_state", "\"State\" IN ('Pending', 'InFlight', 'Accepted', 'Playing', 'Completed', 'Stopped', 'Rejected', 'Failed', 'Expired')");
                    table.ForeignKey(
                        name: "FK_command_deliveries_alert_commands_CommandId",
                        column: x => x.CommandId,
                        principalTable: "alert_commands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_command_deliveries_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_command_deliveries_CommandId_DeviceId",
                table: "command_deliveries",
                columns: new[] { "CommandId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_command_deliveries_DeviceId",
                table: "command_deliveries",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_command_deliveries_State_NextAttemptAtUtc_ExpiresAtUtc",
                table: "command_deliveries",
                columns: new[] { "State", "NextAttemptAtUtc", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "command_deliveries");
        }
    }
}
