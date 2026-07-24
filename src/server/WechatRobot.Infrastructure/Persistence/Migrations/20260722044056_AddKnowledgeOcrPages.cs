using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeOcrPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "knowledge_ocr_page",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    KnowledgeDocumentVersionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PageNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    BlocksJson = table.Column<string>(type: "json", nullable: false),
                    Error = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_ocr_page", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_ocr_page_knowledge_document_version_KnowledgeDocum~",
                        column: x => x.KnowledgeDocumentVersionId,
                        principalTable: "knowledge_document_version",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_ocr_page_KnowledgeDocumentVersionId_PageNumber",
                table: "knowledge_ocr_page",
                columns: new[] { "KnowledgeDocumentVersionId", "PageNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_ocr_page");
        }
    }
}
