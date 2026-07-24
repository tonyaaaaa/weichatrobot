using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "knowledge_document",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Title = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    ActiveVersionId = table.Column<Guid>(type: "char(36)", nullable: true),
                    IsDeleteRequested = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_document", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "knowledge_document_version",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    KnowledgeDocumentId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OriginalFileName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    SafeFileName = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Sha256 = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ObjectKey = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false),
                    PublicUrl = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    FailureReason = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true),
                    StagedContent = table.Column<byte[]>(type: "longblob", nullable: false),
                    IsPublished = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_document_version", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_document_version_knowledge_document_KnowledgeDocum~",
                        column: x => x.KnowledgeDocumentId,
                        principalTable: "knowledge_document",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "knowledge_chunk",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    KnowledgeDocumentVersionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    PageNumber = table.Column<int>(type: "int", nullable: true),
                    Text = table.Column<string>(type: "longtext", nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_chunk", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_chunk_knowledge_document_version_KnowledgeDocument~",
                        column: x => x.KnowledgeDocumentVersionId,
                        principalTable: "knowledge_document_version",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "knowledge_chunk_tag",
                columns: table => new
                {
                    KnowledgeChunkId = table.Column<Guid>(type: "char(36)", nullable: false),
                    KnowledgeTagId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_chunk_tag", x => new { x.KnowledgeChunkId, x.KnowledgeTagId });
                    table.ForeignKey(
                        name: "FK_knowledge_chunk_tag_knowledge_chunk_KnowledgeChunkId",
                        column: x => x.KnowledgeChunkId,
                        principalTable: "knowledge_chunk",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_knowledge_chunk_tag_knowledge_tag_KnowledgeTagId",
                        column: x => x.KnowledgeTagId,
                        principalTable: "knowledge_tag",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_chunk_KnowledgeDocumentVersionId_Sequence",
                table: "knowledge_chunk",
                columns: new[] { "KnowledgeDocumentVersionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_chunk_tag_KnowledgeTagId",
                table: "knowledge_chunk_tag",
                column: "KnowledgeTagId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_Status",
                table: "knowledge_document",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_version_KnowledgeDocumentId_Version",
                table: "knowledge_document_version",
                columns: new[] { "KnowledgeDocumentId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_version_Sha256",
                table: "knowledge_document_version",
                column: "Sha256",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_chunk_tag");

            migrationBuilder.DropTable(
                name: "knowledge_chunk");

            migrationBuilder.DropTable(
                name: "knowledge_document_version");

            migrationBuilder.DropTable(
                name: "knowledge_document");
        }
    }
}
