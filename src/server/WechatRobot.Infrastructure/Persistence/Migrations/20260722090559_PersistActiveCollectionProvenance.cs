using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistActiveCollectionProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PreviousActiveCollectionExclusive",
                table: "knowledge_index_job",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IndexCollectionExclusive",
                table: "knowledge_document_version",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ActiveCollectionExclusive",
                table: "knowledge_document",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousActiveCollectionExclusive",
                table: "knowledge_index_job");

            migrationBuilder.DropColumn(
                name: "IndexCollectionExclusive",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "ActiveCollectionExclusive",
                table: "knowledge_document");
        }
    }
}
