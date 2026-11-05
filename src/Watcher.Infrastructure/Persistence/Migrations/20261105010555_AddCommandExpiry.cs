using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAtUtc",
                table: "alert_commands",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE alert_commands SET \"ExpiresAtUtc\" = \"CreatedAtUtc\" + INTERVAL '5 minutes'");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "ExpiresAtUtc",
                table: "alert_commands",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiresAtUtc",
                table: "alert_commands");
        }
    }
}
