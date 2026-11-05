using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationCommandSequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "command_targets",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql("""
                WITH ranked_targets AS (
                    SELECT target."Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY target."LocationId"
                               ORDER BY command."CreatedAtUtc", command."Id", target."Id") AS sequence
                    FROM command_targets AS target
                    INNER JOIN alert_commands AS command ON command."Id" = target."CommandId"
                )
                UPDATE command_targets AS target
                SET "Sequence" = ranked_targets.sequence
                FROM ranked_targets
                WHERE target."Id" = ranked_targets."Id";
                """);

            migrationBuilder.AlterColumn<long>(
                name: "Sequence",
                table: "command_targets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "location_command_sequences",
                columns: table => new
                {
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    NextSequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location_command_sequences", x => x.LocationId);
                    table.CheckConstraint("CK_location_command_sequences_next_sequence_positive", "\"NextSequence\" > 0");
                    table.ForeignKey(
                        name: "FK_location_command_sequences_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                INSERT INTO location_command_sequences ("LocationId", "NextSequence")
                SELECT "LocationId", MAX("Sequence") + 1
                FROM command_targets
                GROUP BY "LocationId";
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION assign_location_command_sequence() RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF NEW."Sequence" <= 0 THEN
                        INSERT INTO location_command_sequences ("LocationId", "NextSequence")
                        VALUES (NEW."LocationId", 2)
                        ON CONFLICT ("LocationId") DO UPDATE
                        SET "NextSequence" = location_command_sequences."NextSequence" + 1
                        RETURNING "NextSequence" - 1 INTO NEW."Sequence";
                    ELSE
                        INSERT INTO location_command_sequences ("LocationId", "NextSequence")
                        VALUES (NEW."LocationId", NEW."Sequence" + 1)
                        ON CONFLICT ("LocationId") DO UPDATE
                        SET "NextSequence" = GREATEST(
                            location_command_sequences."NextSequence",
                            EXCLUDED."NextSequence");
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER command_targets_assign_location_sequence
                BEFORE INSERT ON command_targets
                FOR EACH ROW
                EXECUTE FUNCTION assign_location_command_sequence();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_command_targets_LocationId_Sequence",
                table: "command_targets",
                columns: new[] { "LocationId", "Sequence" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_command_targets_sequence_positive",
                table: "command_targets",
                sql: "\"Sequence\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Міграцію послідовностей не можна відкочувати: це видалить підтверджений порядок команд. " +
                "Попередня версія застосунку підтримується через тригер, не через downgrade схеми.");
        }
    }
}
