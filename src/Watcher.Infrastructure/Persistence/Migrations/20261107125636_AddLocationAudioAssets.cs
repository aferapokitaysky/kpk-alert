using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationAudioAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sound_presets_Code",
                table: "sound_presets");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_sound_presets_Code",
                table: "sound_presets",
                column: "Code");

            migrationBuilder.CreateTable(
                name: "location_audio_assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PresetCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location_audio_assets", x => x.Id);
                    table.CheckConstraint("CK_location_audio_assets_duration", "\"DurationSeconds\" BETWEEN 1 AND 900");
                    table.CheckConstraint("CK_location_audio_assets_size", "\"SizeBytes\" BETWEEN 1 AND 104857600");
                    table.ForeignKey(
                        name: "FK_location_audio_assets_locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_location_audio_assets_sound_presets_PresetCode",
                        column: x => x.PresetCode,
                        principalTable: "sound_presets",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_location_audio_assets_LocationId_PresetCode",
                table: "location_audio_assets",
                columns: new[] { "LocationId", "PresetCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_location_audio_assets_PresetCode",
                table: "location_audio_assets",
                column: "PresetCode");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "location_audio_assets");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_sound_presets_Code",
                table: "sound_presets");

            migrationBuilder.CreateIndex(
                name: "IX_sound_presets_Code",
                table: "sound_presets",
                column: "Code",
                unique: true);
        }
    }
}
