using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboundProcessingLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RelatedConversationMessageId",
                table: "durable_job",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcessingState",
                table: "conversation_message",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "completed");

            migrationBuilder.Sql("""
                UPDATE durable_job AS job
                INNER JOIN conversation_message AS message
                    ON JSON_VALID(job.PayloadJson) = 1
                    AND LOWER(JSON_UNQUOTE(JSON_EXTRACT(job.PayloadJson, '$.messageId'))) = LOWER(CAST(message.Id AS CHAR(36)))
                SET job.RelatedConversationMessageId = message.Id
                WHERE job.JobType = 'ProcessInboundMessage' AND job.RelatedConversationMessageId IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE conversation_message AS message
                INNER JOIN durable_job AS job ON job.RelatedConversationMessageId = message.Id
                SET message.ProcessingState = CASE
                    WHEN job.Status IN ('pending', 'retrying', 'leased') THEN job.Status
                    WHEN job.Status IN ('deadLetter', 'cancelled', 'failed') THEN job.Status
                    ELSE 'completed'
                END
                WHERE message.Direction = 'inbound';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_durable_job_RelatedConversationMessageId",
                table: "durable_job",
                column: "RelatedConversationMessageId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_durable_job_conversation_message_RelatedConversationMessageId",
                table: "durable_job",
                column: "RelatedConversationMessageId",
                principalTable: "conversation_message",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_durable_job_conversation_message_RelatedConversationMessageId",
                table: "durable_job");

            migrationBuilder.DropIndex(
                name: "IX_durable_job_RelatedConversationMessageId",
                table: "durable_job");

            migrationBuilder.DropColumn(
                name: "RelatedConversationMessageId",
                table: "durable_job");

            migrationBuilder.DropColumn(
                name: "ProcessingState",
                table: "conversation_message");
        }
    }
}
