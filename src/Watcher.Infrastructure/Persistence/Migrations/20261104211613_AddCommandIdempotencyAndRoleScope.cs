using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandIdempotencyAndRoleScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_user_roles_locations_LocationId",
                table: "user_roles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_user_roles",
                table: "user_roles");

            migrationBuilder.AlterColumn<Guid>(
                name: "LocationId",
                table: "user_roles",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                table: "user_roles",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "IdempotencyKey",
                table: "alert_commands",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.Sql("ALTER TABLE user_roles ALTER COLUMN \"Id\" DROP DEFAULT");
            migrationBuilder.Sql("ALTER TABLE alert_commands ALTER COLUMN \"IdempotencyKey\" DROP DEFAULT");

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_roles",
                table: "user_roles",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_UserId_RoleId_LocationId",
                table: "user_roles",
                columns: new[] { "UserId", "RoleId", "LocationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_commands_IdempotencyKey",
                table: "alert_commands",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_user_roles_locations_LocationId",
                table: "user_roles",
                column: "LocationId",
                principalTable: "locations",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_user_roles_locations_LocationId",
                table: "user_roles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_user_roles",
                table: "user_roles");

            migrationBuilder.DropIndex(
                name: "IX_user_roles_UserId_RoleId_LocationId",
                table: "user_roles");

            migrationBuilder.DropIndex(
                name: "IX_alert_commands_IdempotencyKey",
                table: "alert_commands");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "user_roles");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "alert_commands");

            migrationBuilder.AlterColumn<Guid>(
                name: "LocationId",
                table: "user_roles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_roles",
                table: "user_roles",
                columns: new[] { "UserId", "RoleId", "LocationId" });

            migrationBuilder.AddForeignKey(
                name: "FK_user_roles_locations_LocationId",
                table: "user_roles",
                column: "LocationId",
                principalTable: "locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
