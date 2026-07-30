using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivateKnowledgeBatchActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_knowledge_document_version_SourceConversationMessageId",
                table: "knowledge_document_version");

            migrationBuilder.AddColumn<Guid>(
                name: "PrivateKnowledgeIngestBatchId",
                table: "knowledge_index_job",
                type: "char(36)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_index_job_PrivateKnowledgeIngestBatchId",
                table: "knowledge_index_job",
                column: "PrivateKnowledgeIngestBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_version_SourceConversationMessageId",
                table: "knowledge_document_version",
                column: "SourceConversationMessageId");

            migrationBuilder.AddForeignKey(
                name: "FK_knowledge_index_job_private_knowledge_ingest_batch_PrivateKn~",
                table: "knowledge_index_job",
                column: "PrivateKnowledgeIngestBatchId",
                principalTable: "private_knowledge_ingest_batch",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_knowledge_index_job_private_knowledge_ingest_batch_PrivateKn~",
                table: "knowledge_index_job");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_index_job_PrivateKnowledgeIngestBatchId",
                table: "knowledge_index_job");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_document_version_SourceConversationMessageId",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "PrivateKnowledgeIngestBatchId",
                table: "knowledge_index_job");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_version_SourceConversationMessageId",
                table: "knowledge_document_version",
                column: "SourceConversationMessageId",
                unique: true);
        }
    }
}
