using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryCandidateScopeHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_memory_candidate_legacy_scope_fingerprint",
                table: "memory_candidate");

            migrationBuilder.AddColumn<string>(
                name: "ScopeHash",
                table: "memory_candidate",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE `memory_candidate`
                SET `ScopeHash` = UPPER(SHA2(CONCAT(
                    `ScopeType`, '|',
                    COALESCE(`RobotConfigId`, ''), '|',
                    COALESCE(`GroupProfileId`, ''), '|',
                    COALESCE(`SubjectKey`, '')
                ), 256))
                WHERE `ScopeHash` IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "ScopeHash",
                table: "memory_candidate",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_memory_candidate_scope_fingerprint",
                table: "memory_candidate",
                columns: new[] { "ScopeHash", "MemoryType", "Fingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_memory_candidate_scope_fingerprint",
                table: "memory_candidate");

            migrationBuilder.DropColumn(
                name: "ScopeHash",
                table: "memory_candidate");

            migrationBuilder.CreateIndex(
                name: "UX_memory_candidate_legacy_scope_fingerprint",
                table: "memory_candidate",
                columns: new[] { "ScopeType", "RobotConfigId", "GroupProfileId", "SubjectKey", "MemoryType", "Fingerprint" },
                unique: true);
        }
    }
}
