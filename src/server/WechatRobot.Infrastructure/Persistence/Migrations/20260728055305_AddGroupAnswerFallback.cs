using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupAnswerFallback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FinalNoEvidencePolicy",
                table: "group_profile",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "InsufficientEvidence");

            migrationBuilder.AddColumn<bool>(
                name: "ModelKnowledgeFallbackEnabled",
                table: "group_profile",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WebSearchContentSize",
                table: "group_profile",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Medium");

            migrationBuilder.AddColumn<string>(
                name: "WebSearchDomainFilter",
                table: "group_profile",
                type: "varchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WebSearchEnabled",
                table: "group_profile",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WebSearchRecency",
                table: "group_profile",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "NoLimit");

            migrationBuilder.AddColumn<int>(
                name: "WebSearchResultCount",
                table: "group_profile",
                type: "int",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<bool>(
                name: "WebSearchShowSources",
                table: "group_profile",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinalNoEvidencePolicy",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "ModelKnowledgeFallbackEnabled",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "WebSearchContentSize",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "WebSearchDomainFilter",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "WebSearchEnabled",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "WebSearchRecency",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "WebSearchResultCount",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "WebSearchShowSources",
                table: "group_profile");
        }
    }
}
