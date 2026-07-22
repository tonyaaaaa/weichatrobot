using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeIndexJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmbeddingDimension",
                table: "knowledge_document_version",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IndexCollectionName",
                table: "knowledge_document_version",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VectorDistance",
                table: "knowledge_document_version",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActiveCollectionName",
                table: "knowledge_document",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActiveDistance",
                table: "knowledge_document",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActiveEmbeddingDimension",
                table: "knowledge_document",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "knowledge_index_job",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    KnowledgeDocumentId = table.Column<Guid>(type: "char(36)", nullable: false),
                    KnowledgeDocumentVersionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PreviousActiveVersionId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Operation = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    CollectionName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Dimension = table.Column<int>(type: "int", nullable: false),
                    Distance = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LeaseOwner = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FailureReason = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_index_job", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_index_job_knowledge_document_KnowledgeDocumentId",
                        column: x => x.KnowledgeDocumentId,
                        principalTable: "knowledge_document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_knowledge_index_job_knowledge_document_version_KnowledgeDocu~",
                        column: x => x.KnowledgeDocumentVersionId,
                        principalTable: "knowledge_document_version",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_index_job_KnowledgeDocumentId",
                table: "knowledge_index_job",
                column: "KnowledgeDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_index_job_KnowledgeDocumentVersionId_Operation_Sta~",
                table: "knowledge_index_job",
                columns: new[] { "KnowledgeDocumentVersionId", "Operation", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_index_job_Status_NextAttemptAtUtc",
                table: "knowledge_index_job",
                columns: new[] { "Status", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_index_job");

            migrationBuilder.DropColumn(
                name: "EmbeddingDimension",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "IndexCollectionName",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "VectorDistance",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "ActiveCollectionName",
                table: "knowledge_document");

            migrationBuilder.DropColumn(
                name: "ActiveDistance",
                table: "knowledge_document");

            migrationBuilder.DropColumn(
                name: "ActiveEmbeddingDimension",
                table: "knowledge_document");
        }
    }
}
