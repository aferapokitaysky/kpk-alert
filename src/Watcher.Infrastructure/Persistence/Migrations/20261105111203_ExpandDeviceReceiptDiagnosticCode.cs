using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExpandDeviceReceiptDiagnosticCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DiagnosticCode",
                table: "device_command_receipts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(60)",
                oldMaxLength: 60,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM device_command_receipts
                        WHERE char_length("DiagnosticCode") > 60
                    ) THEN
                        RAISE EXCEPTION 'Неможливо зменшити довжину коду діагностики квитанції до 60 символів: є довші значення.';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "DiagnosticCode",
                table: "device_command_receipts",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);
        }
    }
}
