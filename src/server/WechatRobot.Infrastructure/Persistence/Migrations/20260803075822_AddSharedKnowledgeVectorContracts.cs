using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedKnowledgeVectorContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmbeddingContractKey",
                table: "knowledge_index_job",
                type: "varchar(96)",
                maxLength: 96,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousActiveEmbeddingContractKey",
                table: "knowledge_index_job",
                type: "varchar(96)",
                maxLength: 96,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IndexEmbeddingContractKey",
                table: "knowledge_document_version",
                type: "varchar(96)",
                maxLength: 96,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActiveEmbeddingContractKey",
                table: "knowledge_document",
                type: "varchar(96)",
                maxLength: 96,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmbeddingContractKey",
                table: "knowledge_index_job");

            migrationBuilder.DropColumn(
                name: "PreviousActiveEmbeddingContractKey",
                table: "knowledge_index_job");

            migrationBuilder.DropColumn(
                name: "IndexEmbeddingContractKey",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "ActiveEmbeddingContractKey",
                table: "knowledge_document");
        }
    }
}
