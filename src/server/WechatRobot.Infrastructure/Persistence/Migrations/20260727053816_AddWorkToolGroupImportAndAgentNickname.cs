using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkToolGroupImportAndAgentNickname : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RegistrationSource",
                table: "group_profile",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<DateTime>(
                name: "WorkToolImportedAtUtc",
                table: "group_profile",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WorkToolLastSeenAtUtc",
                table: "group_profile",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkToolDisplayName",
                table: "AspNetUsers",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true,
                collation: "utf8mb4_bin");

            migrationBuilder.AddColumn<DateTime>(
                name: "WorkToolDisplayNameUpdatedAtUtc",
                table: "AspNetUsers",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "group_human_agent",
                columns: table => new
                {
                    GroupProfileId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ApplicationUserId = table.Column<Guid>(type: "char(36)", nullable: false),
                    WorkToolDisplayNameSnapshot = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_bin"),
                    LastVerifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    VerificationStatus = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false, defaultValue: "Stale"),
                    IsDefault = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    DefaultGroupProfileId = table.Column<Guid>(type: "char(36)", nullable: true, computedColumnSql: "CASE WHEN `IsDefault` = 1 AND `IsEnabled` = 1 THEN `GroupProfileId` ELSE NULL END", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_human_agent", x => new { x.GroupProfileId, x.ApplicationUserId });
                    table.CheckConstraint("CK_group_human_agent_verification_status", "`VerificationStatus` IN ('Verified','Missing','Conflict','Stale')");
                    table.ForeignKey(
                        name: "FK_group_human_agent_AspNetUsers_ApplicationUserId",
                        column: x => x.ApplicationUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_group_human_agent_group_profile_GroupProfileId",
                        column: x => x.GroupProfileId,
                        principalTable: "group_profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.AddCheckConstraint(
                name: "CK_group_profile_registration_source",
                table: "group_profile",
                sql: "`RegistrationSource` IN ('Manual','WorkToolImport')");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_WorkToolDisplayName",
                table: "AspNetUsers",
                column: "WorkToolDisplayName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_group_human_agent_ApplicationUserId",
                table: "group_human_agent",
                column: "ApplicationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_group_human_agent_DefaultGroupProfileId",
                table: "group_human_agent",
                column: "DefaultGroupProfileId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_human_agent");

            migrationBuilder.DropCheckConstraint(
                name: "CK_group_profile_registration_source",
                table: "group_profile");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_WorkToolDisplayName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "RegistrationSource",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "WorkToolImportedAtUtc",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "WorkToolLastSeenAtUtc",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "WorkToolDisplayName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "WorkToolDisplayNameUpdatedAtUtc",
                table: "AspNetUsers");
        }
    }
}
