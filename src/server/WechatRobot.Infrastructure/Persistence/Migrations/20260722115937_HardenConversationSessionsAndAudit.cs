using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenConversationSessionsAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SenderExternalUserId",
                table: "conversation_message",
                newName: "SenderDisplayName");

            migrationBuilder.AddColumn<string>(
                name: "InputSummaryJson",
                table: "retrieval_audit",
                type: "json",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE `retrieval_audit`
                SET `InputSummaryJson` = '{}'
                WHERE `InputSummaryJson` IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "InputSummaryJson",
                table: "retrieval_audit",
                type: "json",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "json",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAtUtc",
                table: "conversation_session",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "conversation_session",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextSequence",
                table: "conversation_session",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "conversation_session",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "GroupName",
                table: "conversation_message",
                type: "varchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "SessionSequence",
                table: "conversation_message",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StableSenderId",
                table: "conversation_message",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversation_message_ConversationSessionId_SessionSequence",
                table: "conversation_message",
                columns: new[] { "ConversationSessionId", "SessionSequence" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_conversation_message_ConversationSessionId",
                table: "conversation_message");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_conversation_message_ConversationSessionId",
                table: "conversation_message",
                column: "ConversationSessionId");

            migrationBuilder.DropIndex(
                name: "IX_conversation_message_ConversationSessionId_SessionSequence",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "InputSummaryJson",
                table: "retrieval_audit");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtc",
                table: "conversation_session");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "conversation_session");

            migrationBuilder.DropColumn(
                name: "NextSequence",
                table: "conversation_session");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "conversation_session");

            migrationBuilder.DropColumn(
                name: "GroupName",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "SessionSequence",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "StableSenderId",
                table: "conversation_message");

            migrationBuilder.RenameColumn(
                name: "SenderDisplayName",
                table: "conversation_message",
                newName: "SenderExternalUserId");

        }
    }
}
