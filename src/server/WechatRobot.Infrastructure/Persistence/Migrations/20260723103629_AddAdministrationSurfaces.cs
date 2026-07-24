using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdministrationSurfaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "knowledge_tag",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "administration_audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Actor = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    SanitizedDetailJson = table.Column<string>(type: "json", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administration_audit", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "system_setting",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    ValueJson = table.Column<string>(type: "json", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_setting", x => x.Key);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_administration_audit_CreatedAtUtc",
                table: "administration_audit",
                column: "CreatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "administration_audit");

            migrationBuilder.DropTable(
                name: "system_setting");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "knowledge_tag");
        }
    }
}
