using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watcher.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedRoleCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO roles ("Id", "Code", "Name") VALUES
                    ('6e3b01e9-21aa-4e62-aac0-760700000001', 'admin', 'Адміністратор'),
                    ('6e3b01e9-21aa-4e62-aac0-760700000002', 'auditor', 'Аудитор'),
                    ('6e3b01e9-21aa-4e62-aac0-760700000003', 'notificator', 'Оператор оповіщення'),
                    ('6e3b01e9-21aa-4e62-aac0-760700000004', 'sound_engineer', 'Фахівець зі звуку')
                ON CONFLICT ("Code") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM roles AS role
                WHERE role."Id" IN (
                    '6e3b01e9-21aa-4e62-aac0-760700000001',
                    '6e3b01e9-21aa-4e62-aac0-760700000002',
                    '6e3b01e9-21aa-4e62-aac0-760700000003',
                    '6e3b01e9-21aa-4e62-aac0-760700000004'
                )
                  AND NOT EXISTS (
                      SELECT 1 FROM user_roles AS assignment WHERE assignment."RoleId" = role."Id"
                  );
                """);
        }
    }
}
