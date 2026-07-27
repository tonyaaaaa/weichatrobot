using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeIndexPendingTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingTagIdsJson",
                table: "knowledge_index_job",
                type: "json",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE `knowledge_index_job`
                SET `PendingTagIdsJson` = '[]'
                WHERE `PendingTagIdsJson` IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "PendingTagIdsJson",
                table: "knowledge_index_job",
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
                name: "PendingTagIdsJson",
                table: "knowledge_index_job");
        }
    }
}
