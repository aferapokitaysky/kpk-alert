using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUkraineAlarmWebhookOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_integration_states",
                columns: table => new
                {
                    IntegrationCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    IsRegistered = table.Column<bool>(type: "boolean", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_integration_states", x => x.IntegrationCode);
                    table.CheckConstraint("CK_external_integration_states_integration_code", "\"IntegrationCode\" = 'ukrainealarm_webhook'");
                });

            migrationBuilder.CreateTable(
                name: "ukrainealarm_webhook_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, defaultValue: "ukrainealarm_webhook"),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CallbackUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    OutcomeCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ukrainealarm_webhook_operations", x => x.Id);
                    table.CheckConstraint("CK_ukrainealarm_webhook_operations_action", "\"Action\" IN ('register', 'update')");
                    table.CheckConstraint("CK_ukrainealarm_webhook_operations_state", "\"State\" IN ('Pending', 'Running', 'Succeeded', 'Rejected', 'Unknown', 'Resolved')");
                    table.ForeignKey(
                        name: "FK_ukrainealarm_webhook_operations_external_integration_states~",
                        column: x => x.IntegrationCode,
                        principalTable: "external_integration_states",
                        principalColumn: "IntegrationCode",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ukrainealarm_webhook_operations_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                INSERT INTO external_integration_states ("IntegrationCode", "IsRegistered", "UpdatedAtUtc")
                VALUES ('ukrainealarm_webhook', NULL, CURRENT_TIMESTAMP);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ukrainealarm_webhook_operations_ActorUserId_CreatedAtUtc",
                table: "ukrainealarm_webhook_operations",
                columns: new[] { "ActorUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ukrainealarm_webhook_operations_IdempotencyKey",
                table: "ukrainealarm_webhook_operations",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ukrainealarm_webhook_operations_IntegrationCode",
                table: "ukrainealarm_webhook_operations",
                column: "IntegrationCode",
                unique: true,
                filter: "\"State\" IN ('Pending', 'Running', 'Unknown')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $migration$
                BEGIN
                    IF EXISTS (SELECT 1 FROM ukrainealarm_webhook_operations)
                       OR EXISTS (SELECT 1 FROM external_integration_states WHERE "IsRegistered" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Відкат неможливий, поки зберігається історія операцій webhook.'
                            USING ERRCODE = '55000';
                    END IF;
                END
                $migration$;
                """);

            migrationBuilder.DropTable(
                name: "ukrainealarm_webhook_operations");

            migrationBuilder.DropTable(
                name: "external_integration_states");
        }
    }
}
