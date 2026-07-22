using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupRulesTagsAndContextSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RuleKind",
                table: "group_rule",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ContextHistoryTurns",
                table: "group_profile",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContextIdleTimeoutMinutes",
                table: "group_profile",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ContextIncludeBotHistory",
                table: "group_profile",
                type: "tinyint(1)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ContextSenderIsolated",
                table: "group_profile",
                type: "tinyint(1)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ContextSummaryEnabled",
                table: "group_profile",
                type: "tinyint(1)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContextTokenCap",
                table: "group_profile",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "group_profile_tag",
                columns: table => new
                {
                    GroupProfileId = table.Column<Guid>(type: "char(36)", nullable: false),
                    KnowledgeTagId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_profile_tag", x => new { x.GroupProfileId, x.KnowledgeTagId });
                    table.ForeignKey(
                        name: "FK_group_profile_tag_group_profile_GroupProfileId",
                        column: x => x.GroupProfileId,
                        principalTable: "group_profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_profile_tag_knowledge_tag_KnowledgeTagId",
                        column: x => x.KnowledgeTagId,
                        principalTable: "knowledge_tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_group_profile_tag_KnowledgeTagId",
                table: "group_profile_tag",
                column: "KnowledgeTagId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_profile_tag");

            migrationBuilder.DropColumn(
                name: "RuleKind",
                table: "group_rule");

            migrationBuilder.DropColumn(
                name: "ContextHistoryTurns",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "ContextIdleTimeoutMinutes",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "ContextIncludeBotHistory",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "ContextSenderIsolated",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "ContextSummaryEnabled",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "ContextTokenCap",
                table: "group_profile");
        }
    }
}
