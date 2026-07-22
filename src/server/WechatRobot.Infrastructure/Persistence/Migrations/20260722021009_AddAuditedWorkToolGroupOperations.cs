using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditedWorkToolGroupOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptedCredential",
                table: "robot_config",
                type: "longtext",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "worktool_operation_audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    OperatorName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    Operation = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    SanitizedRequestJson = table.Column<string>(type: "longtext", nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Result = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worktool_operation_audit", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "worktool_operation_confirmation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TokenHash = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    OperatorName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    PayloadHash = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worktool_operation_confirmation", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_worktool_operation_audit_CreatedAtUtc",
                table: "worktool_operation_audit",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_worktool_operation_confirmation_TokenHash",
                table: "worktool_operation_confirmation",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "worktool_operation_audit");

            migrationBuilder.DropTable(
                name: "worktool_operation_confirmation");

            migrationBuilder.DropColumn(
                name: "EncryptedCredential",
                table: "robot_config");
        }
    }
}
