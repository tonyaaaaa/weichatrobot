using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAnswerSourceAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnswerSource",
                table: "retrieval_audit",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<string>(
                name: "WebSearchFailureCode",
                table: "retrieval_audit",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebSearchSourcesJson",
                table: "retrieval_audit",
                type: "json",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE `retrieval_audit` SET `WebSearchSourcesJson` = JSON_ARRAY() WHERE `WebSearchSourcesJson` IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "WebSearchSourcesJson",
                table: "retrieval_audit",
                type: "json",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "json",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnswerSource",
                table: "retrieval_audit");

            migrationBuilder.DropColumn(
                name: "WebSearchFailureCode",
                table: "retrieval_audit");

            migrationBuilder.DropColumn(
                name: "WebSearchSourcesJson",
                table: "retrieval_audit");
        }
    }
}
