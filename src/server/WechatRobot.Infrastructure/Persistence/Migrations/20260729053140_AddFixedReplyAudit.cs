using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFixedReplyAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FixedReplyTemplateId",
                table: "retrieval_audit",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FixedReplyTemplateVersion",
                table: "retrieval_audit",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_audit_FixedReplyTemplateId",
                table: "retrieval_audit",
                column: "FixedReplyTemplateId");

            migrationBuilder.AddForeignKey(
                name: "FK_retrieval_audit_fixed_reply_template_FixedReplyTemplateId",
                table: "retrieval_audit",
                column: "FixedReplyTemplateId",
                principalTable: "fixed_reply_template",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_retrieval_audit_fixed_reply_template_FixedReplyTemplateId",
                table: "retrieval_audit");

            migrationBuilder.DropIndex(
                name: "IX_retrieval_audit_FixedReplyTemplateId",
                table: "retrieval_audit");

            migrationBuilder.DropColumn(
                name: "FixedReplyTemplateId",
                table: "retrieval_audit");

            migrationBuilder.DropColumn(
                name: "FixedReplyTemplateVersion",
                table: "retrieval_audit");
        }
    }
}
