using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceCommandReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_command_receipts",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DiagnosticCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_command_receipts", x => new { x.DeviceId, x.EventId });
                    table.CheckConstraint("CK_device_command_receipts_sequence", "\"Sequence\" > 0");
                    table.CheckConstraint("CK_device_command_receipts_state", "\"State\" IN ('Accepted', 'Playing', 'Completed', 'Stopped', 'Rejected', 'Failed', 'Expired')");
                    table.ForeignKey(
                        name: "FK_device_command_receipts_alert_commands_CommandId",
                        column: x => x.CommandId,
                        principalTable: "alert_commands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_device_command_receipts_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_device_command_receipts_CommandId_DeviceId_ReceivedAtUtc",
                table: "device_command_receipts",
                columns: new[] { "CommandId", "DeviceId", "ReceivedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_command_receipts");
        }
    }
}
