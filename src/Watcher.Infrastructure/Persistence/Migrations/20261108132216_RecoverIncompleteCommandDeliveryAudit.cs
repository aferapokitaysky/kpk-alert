using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecoverIncompleteCommandDeliveryAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastWriteOutcome",
                table: "command_deliveries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE command_deliveries AS delivery
                SET "LastWriteOutcome" = result."Outcome"
                FROM (
                    SELECT DISTINCT ON ("Payload"->>'DeliveryId', "Payload"->>'AttemptCount')
                        "Payload"->>'DeliveryId' AS "DeliveryId",
                        "Payload"->>'AttemptCount' AS "AttemptCount",
                        "Payload"->>'Outcome' AS "Outcome"
                    FROM audit_events
                    WHERE "EventType" = 'alert_command.delivery_write_result'
                      AND "Payload" ? 'DeliveryId'
                      AND "Payload" ? 'AttemptCount'
                    ORDER BY "Payload"->>'DeliveryId', "Payload"->>'AttemptCount', "OccurredAtUtc" DESC
                ) AS result
                WHERE delivery."Id"::text = result."DeliveryId"
                  AND delivery."AttemptCount"::text = result."AttemptCount"
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastWriteOutcome",
                table: "command_deliveries");
        }
    }
}
