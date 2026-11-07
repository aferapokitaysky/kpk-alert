using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingDeviceCertificateRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PendingCertificateCreatedAtUtc",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingCertificateThumbprint",
                table: "devices",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_devices_PendingCertificateThumbprint",
                table: "devices",
                column: "PendingCertificateThumbprint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_devices_PendingCertificateThumbprint",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "PendingCertificateCreatedAtUtc",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "PendingCertificateThumbprint",
                table: "devices");
        }
    }
}
