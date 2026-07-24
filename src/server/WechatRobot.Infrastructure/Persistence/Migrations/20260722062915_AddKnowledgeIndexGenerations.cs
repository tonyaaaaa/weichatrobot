using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeIndexGenerations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Generation",
                table: "knowledge_index_job",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PreviousActiveCollectionName",
                table: "knowledge_index_job",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousActiveDistance",
                table: "knowledge_index_job",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviousActiveEmbeddingDimension",
                table: "knowledge_index_job",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IndexGeneration",
                table: "knowledge_document_version",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActiveIndexGeneration",
                table: "knowledge_document",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Generation",
                table: "knowledge_index_job");

            migrationBuilder.DropColumn(
                name: "PreviousActiveCollectionName",
                table: "knowledge_index_job");

            migrationBuilder.DropColumn(
                name: "PreviousActiveDistance",
                table: "knowledge_index_job");

            migrationBuilder.DropColumn(
                name: "PreviousActiveEmbeddingDimension",
                table: "knowledge_index_job");

            migrationBuilder.DropColumn(
                name: "IndexGeneration",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "ActiveIndexGeneration",
                table: "knowledge_document");
        }
    }
}
