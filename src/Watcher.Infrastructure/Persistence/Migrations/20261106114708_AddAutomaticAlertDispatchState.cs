using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticAlertDispatchState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "automatic_alert_dispatch_states",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    HasOpenEpisode = table.Column<bool>(type: "boolean", nullable: false),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ScopeId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    StartCommandId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastDecisionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automatic_alert_dispatch_states", x => x.Id);
                    table.CheckConstraint("CK_automatic_alert_dispatch_states_episode", "(\"HasOpenEpisode\" AND \"Scope\" IS NOT NULL AND \"ScopeId\" IS NOT NULL AND \"StartCommandId\" IS NOT NULL AND \"StartedAtUtc\" IS NOT NULL) OR (NOT \"HasOpenEpisode\" AND \"Scope\" IS NULL AND \"ScopeId\" IS NULL AND \"StartCommandId\" IS NULL AND \"StartedAtUtc\" IS NULL)");
                    table.CheckConstraint("CK_automatic_alert_dispatch_states_id", "\"Id\" = 'kremenchuk'");
                });

            migrationBuilder.Sql("""
                INSERT INTO automatic_alert_dispatch_states
                    ("Id", "HasOpenEpisode", "LastDecisionCode", "UpdatedAtUtc")
                VALUES ('kremenchuk', false, 'not_evaluated', CURRENT_TIMESTAMP);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automatic_alert_dispatch_states");
        }
    }
}
