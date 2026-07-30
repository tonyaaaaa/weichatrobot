using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivateRetrievalAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "GroupProfileId",
                table: "retrieval_audit",
                type: "char(36)",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "char(36)");

            migrationBuilder.AddColumn<string>(
                name: "ChannelType",
                table: "retrieval_audit",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Group");

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_audit_ChannelType_GroupProfileId_CreatedAtUtc",
                table: "retrieval_audit",
                columns: new[] { "ChannelType", "GroupProfileId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_audit_GroupProfileId",
                table: "retrieval_audit",
                column: "GroupProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_retrieval_audit_ChannelType_GroupProfileId_CreatedAtUtc",
                table: "retrieval_audit");

            migrationBuilder.DropIndex(
                name: "IX_retrieval_audit_GroupProfileId",
                table: "retrieval_audit");

            migrationBuilder.DropColumn(
                name: "ChannelType",
                table: "retrieval_audit");

            migrationBuilder.AlterColumn<Guid>(
                name: "GroupProfileId",
                table: "retrieval_audit",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true);

        }
    }
}
