using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageIntentAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_intent_audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConversationMessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    GroupProfileId = table.Column<Guid>(type: "char(36)", nullable: false),
                    IntentDecision = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    IntentCategory = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    IntentReasonCode = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    IntentConfidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    FailureCode = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    IntentRuntimeMode = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    IntentAgentVersion = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    IntentModelConfigurationId = table.Column<Guid>(type: "char(36)", nullable: true),
                    IntentModelVersion = table.Column<int>(type: "int", nullable: true),
                    IntentLatencyMilliseconds = table.Column<int>(type: "int", nullable: false),
                    FormalConversationIncluded = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IntentDecidedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_intent_audit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_message_intent_audit_conversation_message_ConversationMessag~",
                        column: x => x.ConversationMessageId,
                        principalTable: "conversation_message",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_message_intent_audit_group_profile_GroupProfileId",
                        column: x => x.GroupProfileId,
                        principalTable: "group_profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_message_intent_audit_model_config_IntentModelConfigurationId",
                        column: x => x.IntentModelConfigurationId,
                        principalTable: "model_config",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_intent_audit_diagnostics",
                table: "message_intent_audit",
                columns: new[] { "GroupProfileId", "IntentRuntimeMode", "IntentDecision", "IntentDecidedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_message_intent_audit_ConversationMessageId",
                table: "message_intent_audit",
                column: "ConversationMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_message_intent_audit_IntentModelConfigurationId",
                table: "message_intent_audit",
                column: "IntentModelConfigurationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_intent_audit");
        }
    }
}
