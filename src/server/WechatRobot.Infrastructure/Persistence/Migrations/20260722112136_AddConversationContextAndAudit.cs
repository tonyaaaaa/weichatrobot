using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationContextAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConversationSessionId",
                table: "conversation_message",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Direction",
                table: "conversation_message",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "inbound");

            migrationBuilder.AddColumn<Guid>(
                name: "InReplyToMessageId",
                table: "conversation_message",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "conversation_message",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "user");

            migrationBuilder.CreateTable(
                name: "conversation_session",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    GroupProfileId = table.Column<Guid>(type: "char(36)", nullable: false),
                    SenderScopeKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Summary = table.Column<string>(type: "longtext", nullable: true),
                    ClearedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastActivityAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_session", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversation_session_group_profile_GroupProfileId",
                        column: x => x.GroupProfileId,
                        principalTable: "group_profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "retrieval_audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConversationMessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    GroupProfileId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Decision = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    ConfidenceThreshold = table.Column<double>(type: "double", nullable: false),
                    ConfidenceValue = table.Column<double>(type: "double", nullable: true),
                    ContextPolicy = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false),
                    FailureCode = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    EvidenceJson = table.Column<string>(type: "json", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retrieval_audit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_retrieval_audit_conversation_message_ConversationMessageId",
                        column: x => x.ConversationMessageId,
                        principalTable: "conversation_message",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_retrieval_audit_group_profile_GroupProfileId",
                        column: x => x.GroupProfileId,
                        principalTable: "group_profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_message_ConversationSessionId",
                table: "conversation_message",
                column: "ConversationSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_message_InReplyToMessageId",
                table: "conversation_message",
                column: "InReplyToMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversation_session_GroupProfileId_SenderScopeKey",
                table: "conversation_session",
                columns: new[] { "GroupProfileId", "SenderScopeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_audit_ConversationMessageId",
                table: "retrieval_audit",
                column: "ConversationMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_audit_GroupProfileId_CreatedAtUtc",
                table: "retrieval_audit",
                columns: new[] { "GroupProfileId", "CreatedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_conversation_message_conversation_session_ConversationSessio~",
                table: "conversation_message",
                column: "ConversationSessionId",
                principalTable: "conversation_session",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_conversation_message_conversation_session_ConversationSessio~",
                table: "conversation_message");

            migrationBuilder.DropTable(
                name: "conversation_session");

            migrationBuilder.DropTable(
                name: "retrieval_audit");

            migrationBuilder.DropIndex(
                name: "IX_conversation_message_ConversationSessionId",
                table: "conversation_message");

            migrationBuilder.DropIndex(
                name: "IX_conversation_message_InReplyToMessageId",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "ConversationSessionId",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "Direction",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "InReplyToMessageId",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "conversation_message");
        }
    }
}
