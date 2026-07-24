using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeIndexCleanupSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceIndexJobId",
                table: "knowledge_index_job",
                type: "char(36)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_index_job_SourceIndexJobId",
                table: "knowledge_index_job",
                column: "SourceIndexJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_knowledge_index_job_SourceIndexJobId",
                table: "knowledge_index_job");

            migrationBuilder.DropColumn(
                name: "SourceIndexJobId",
                table: "knowledge_index_job");
        }
    }
}
