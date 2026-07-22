using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHandoffsAndReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "handoff_case",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    QuestionMessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    RobotConfigId = table.Column<Guid>(type: "char(36)", nullable: false),
                    GroupProfileId = table.Column<Guid>(type: "char(36)", nullable: false),
                    State = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    EvidenceJson = table.Column<string>(type: "json", nullable: false),
                    PauseScope = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    StableSenderId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    AssigneeUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    FinalAnswer = table.Column<string>(type: "longtext", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_handoff_case", x => x.Id);
                    table.ForeignKey(
                        name: "FK_handoff_case_conversation_message_QuestionMessageId",
                        column: x => x.QuestionMessageId,
                        principalTable: "conversation_message",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_handoff_case_group_profile_GroupProfileId",
                        column: x => x.GroupProfileId,
                        principalTable: "group_profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_handoff_case_robot_config_RobotConfigId",
                        column: x => x.RobotConfigId,
                        principalTable: "robot_config",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "handoff_message",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    HandoffCaseId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ExternalMessageId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    SenderDisplayName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    AuthenticatedUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    AuthenticationKind = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "longtext", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_handoff_message", x => x.Id);
                    table.ForeignKey(
                        name: "FK_handoff_message_handoff_case_HandoffCaseId",
                        column: x => x.HandoffCaseId,
                        principalTable: "handoff_case",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "knowledge_candidate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    HandoffCaseId = table.Column<Guid>(type: "char(36)", nullable: false),
                    QuestionMessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Question = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false),
                    Answer = table.Column<string>(type: "longtext", nullable: false),
                    EvidenceJson = table.Column<string>(type: "json", nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    KnowledgeDocumentVersionId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_candidate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_candidate_conversation_message_QuestionMessageId",
                        column: x => x.QuestionMessageId,
                        principalTable: "conversation_message",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_knowledge_candidate_handoff_case_HandoffCaseId",
                        column: x => x.HandoffCaseId,
                        principalTable: "handoff_case",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_knowledge_candidate_knowledge_document_version_KnowledgeDocu~",
                        column: x => x.KnowledgeDocumentVersionId,
                        principalTable: "knowledge_document_version",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "knowledge_review",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    KnowledgeCandidateId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ReviewerUserId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Decision = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    TagIdsJson = table.Column<string>(type: "json", nullable: false),
                    RevisedAnswer = table.Column<string>(type: "longtext", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_review", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_review_knowledge_candidate_KnowledgeCandidateId",
                        column: x => x.KnowledgeCandidateId,
                        principalTable: "knowledge_candidate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_handoff_case_GroupProfileId_State",
                table: "handoff_case",
                columns: new[] { "GroupProfileId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_handoff_case_QuestionMessageId",
                table: "handoff_case",
                column: "QuestionMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_handoff_case_RobotConfigId",
                table: "handoff_case",
                column: "RobotConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_handoff_message_ExternalMessageId",
                table: "handoff_message",
                column: "ExternalMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_handoff_message_HandoffCaseId",
                table: "handoff_message",
                column: "HandoffCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_candidate_HandoffCaseId",
                table: "knowledge_candidate",
                column: "HandoffCaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_candidate_KnowledgeDocumentVersionId",
                table: "knowledge_candidate",
                column: "KnowledgeDocumentVersionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_candidate_QuestionMessageId",
                table: "knowledge_candidate",
                column: "QuestionMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_review_IdempotencyKey",
                table: "knowledge_review",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_review_KnowledgeCandidateId",
                table: "knowledge_review",
                column: "KnowledgeCandidateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "handoff_message");

            migrationBuilder.DropTable(
                name: "knowledge_review");

            migrationBuilder.DropTable(
                name: "knowledge_candidate");

            migrationBuilder.DropTable(
                name: "handoff_case");
        }
    }
}
