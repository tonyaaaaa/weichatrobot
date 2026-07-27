using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeChunkPreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PreviewRevision",
                table: "knowledge_document_version",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Answer",
                table: "knowledge_chunk",
                type: "longtext",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HeadingsJson",
                table: "knowledge_chunk",
                type: "json",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE `knowledge_chunk`
                SET `HeadingsJson` = '[]'
                WHERE `HeadingsJson` IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "HeadingsJson",
                table: "knowledge_chunk",
                type: "json",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "json",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsTable",
                table: "knowledge_chunk",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Question",
                table: "knowledge_chunk",
                type: "varchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SynonymsJson",
                table: "knowledge_chunk",
                type: "json",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE `knowledge_chunk`
                SET `SynonymsJson` = '[]'
                WHERE `SynonymsJson` IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "SynonymsJson",
                table: "knowledge_chunk",
                type: "json",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "json",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TableColumns",
                table: "knowledge_chunk",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TableRows",
                table: "knowledge_chunk",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "knowledge_chunk_preview",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    KnowledgeDocumentVersionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    PageNumber = table.Column<int>(type: "int", nullable: true),
                    Text = table.Column<string>(type: "longtext", nullable: false),
                    HeadingsJson = table.Column<string>(type: "json", nullable: false),
                    IsTable = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    TableRows = table.Column<int>(type: "int", nullable: true),
                    TableColumns = table.Column<int>(type: "int", nullable: true),
                    Question = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true),
                    SynonymsJson = table.Column<string>(type: "json", nullable: false),
                    Answer = table.Column<string>(type: "longtext", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_chunk_preview", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_chunk_preview_knowledge_document_version_Knowledge~",
                        column: x => x.KnowledgeDocumentVersionId,
                        principalTable: "knowledge_document_version",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_chunk_preview_KnowledgeDocumentVersionId_Sequence",
                table: "knowledge_chunk_preview",
                columns: new[] { "KnowledgeDocumentVersionId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_chunk_preview");

            migrationBuilder.DropColumn(
                name: "PreviewRevision",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "Answer",
                table: "knowledge_chunk");

            migrationBuilder.DropColumn(
                name: "HeadingsJson",
                table: "knowledge_chunk");

            migrationBuilder.DropColumn(
                name: "IsTable",
                table: "knowledge_chunk");

            migrationBuilder.DropColumn(
                name: "Question",
                table: "knowledge_chunk");

            migrationBuilder.DropColumn(
                name: "SynonymsJson",
                table: "knowledge_chunk");

            migrationBuilder.DropColumn(
                name: "TableColumns",
                table: "knowledge_chunk");

            migrationBuilder.DropColumn(
                name: "TableRows",
                table: "knowledge_chunk");
        }
    }
}
