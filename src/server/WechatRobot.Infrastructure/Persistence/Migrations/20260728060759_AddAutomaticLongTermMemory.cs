using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticLongTermMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "HandoffCaseId",
                table: "knowledge_candidate",
                type: "char(36)",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "char(36)");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceConversationMessageId",
                table: "knowledge_candidate",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceMemoryCandidateId",
                table: "knowledge_candidate",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "knowledge_candidate",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "HistoricalHandoff");

            migrationBuilder.CreateTable(
                name: "memory_audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Action = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    ActorType = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    TargetType = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    TargetId = table.Column<Guid>(type: "char(36)", nullable: false),
                    OldStatus = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    NewStatus = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ReasonCode = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_audit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_memory_audit_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "memory_candidate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    ScopeType = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    RobotConfigId = table.Column<Guid>(type: "char(36)", nullable: true),
                    GroupProfileId = table.Column<Guid>(type: "char(36)", nullable: true),
                    SubjectKey = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    SubjectDisplayName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    MemoryType = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Content = table.Column<string>(type: "longtext", nullable: false),
                    NormalizedKey = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false),
                    Fingerprint = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Confidence = table.Column<double>(type: "double", nullable: false),
                    IsExplicit = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ObservationCount = table.Column<int>(type: "int", nullable: false),
                    DistinctSessionCount = table.Column<int>(type: "int", nullable: false),
                    DistinctDayCount = table.Column<int>(type: "int", nullable: false),
                    HasUnresolvedConflict = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    PromotedMemoryEntryId = table.Column<Guid>(type: "char(36)", nullable: true),
                    KnowledgeCandidateId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_candidate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_memory_candidate_group_profile_GroupProfileId",
                        column: x => x.GroupProfileId,
                        principalTable: "group_profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_memory_candidate_robot_config_RobotConfigId",
                        column: x => x.RobotConfigId,
                        principalTable: "robot_config",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "memory_entry",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    ScopeType = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    RobotConfigId = table.Column<Guid>(type: "char(36)", nullable: true),
                    GroupProfileId = table.Column<Guid>(type: "char(36)", nullable: true),
                    SubjectKey = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    SubjectDisplayName = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true),
                    MemoryType = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Content = table.Column<string>(type: "longtext", nullable: false),
                    NormalizedKey = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false),
                    Confidence = table.Column<double>(type: "double", nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    SupersedesMemoryEntryId = table.Column<Guid>(type: "char(36)", nullable: true),
                    SourceCandidateId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ValidFromUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RecallCount = table.Column<int>(type: "int", nullable: false),
                    LastRecalledAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    StatusVersion = table.Column<int>(type: "int", nullable: false),
                    IndexGeneration = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_entry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_memory_entry_group_profile_GroupProfileId",
                        column: x => x.GroupProfileId,
                        principalTable: "group_profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_memory_entry_memory_candidate_SourceCandidateId",
                        column: x => x.SourceCandidateId,
                        principalTable: "memory_candidate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_memory_entry_memory_entry_SupersedesMemoryEntryId",
                        column: x => x.SupersedesMemoryEntryId,
                        principalTable: "memory_entry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_memory_entry_robot_config_RobotConfigId",
                        column: x => x.RobotConfigId,
                        principalTable: "robot_config",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "memory_observation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    MemoryCandidateId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConversationSessionId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ConversationMessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    SourceContentHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    EvidenceSummary = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ModelConfigurationId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_observation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_memory_observation_conversation_message_ConversationMessageId",
                        column: x => x.ConversationMessageId,
                        principalTable: "conversation_message",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_memory_observation_conversation_session_ConversationSessionId",
                        column: x => x.ConversationSessionId,
                        principalTable: "conversation_session",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_memory_observation_memory_candidate_MemoryCandidateId",
                        column: x => x.MemoryCandidateId,
                        principalTable: "memory_candidate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_observation_model_config_ModelConfigurationId",
                        column: x => x.ModelConfigurationId,
                        principalTable: "model_config",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_candidate_SourceConversationMessageId",
                table: "knowledge_candidate",
                column: "SourceConversationMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_candidate_SourceMemoryCandidateId",
                table: "knowledge_candidate",
                column: "SourceMemoryCandidateId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_audit_ActorUserId",
                table: "memory_audit",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_audit_TargetType_TargetId_CreatedAtUtc",
                table: "memory_audit",
                columns: new[] { "TargetType", "TargetId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_candidate_GroupProfileId",
                table: "memory_candidate",
                column: "GroupProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_candidate_KnowledgeCandidateId",
                table: "memory_candidate",
                column: "KnowledgeCandidateId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_candidate_PromotedMemoryEntryId",
                table: "memory_candidate",
                column: "PromotedMemoryEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_candidate_RobotConfigId",
                table: "memory_candidate",
                column: "RobotConfigId");

            migrationBuilder.CreateIndex(
                name: "UX_memory_candidate_legacy_scope_fingerprint",
                table: "memory_candidate",
                columns: new[] { "ScopeType", "RobotConfigId", "GroupProfileId", "SubjectKey", "MemoryType", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_candidate_Status_UpdatedAtUtc",
                table: "memory_candidate",
                columns: new[] { "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_entry_GroupProfileId",
                table: "memory_entry",
                column: "GroupProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_entry_RobotConfigId",
                table: "memory_entry",
                column: "RobotConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_entry_ScopeType_RobotConfigId_GroupProfileId_SubjectK~",
                table: "memory_entry",
                columns: new[] { "ScopeType", "RobotConfigId", "GroupProfileId", "SubjectKey", "MemoryType" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_entry_SourceCandidateId",
                table: "memory_entry",
                column: "SourceCandidateId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_entry_Status_ExpiresAtUtc",
                table: "memory_entry",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_entry_SupersedesMemoryEntryId",
                table: "memory_entry",
                column: "SupersedesMemoryEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_observation_ConversationMessageId",
                table: "memory_observation",
                column: "ConversationMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_observation_ConversationSessionId",
                table: "memory_observation",
                column: "ConversationSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_observation_MemoryCandidateId_ConversationMessageId",
                table: "memory_observation",
                columns: new[] { "MemoryCandidateId", "ConversationMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memory_observation_MemoryCandidateId_ConversationSessionId_O~",
                table: "memory_observation",
                columns: new[] { "MemoryCandidateId", "ConversationSessionId", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_observation_ModelConfigurationId",
                table: "memory_observation",
                column: "ModelConfigurationId");

            migrationBuilder.AddForeignKey(
                name: "FK_knowledge_candidate_conversation_message_SourceConversationM~",
                table: "knowledge_candidate",
                column: "SourceConversationMessageId",
                principalTable: "conversation_message",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_knowledge_candidate_memory_candidate_SourceMemoryCandidateId",
                table: "knowledge_candidate",
                column: "SourceMemoryCandidateId",
                principalTable: "memory_candidate",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_knowledge_candidate_conversation_message_SourceConversationM~",
                table: "knowledge_candidate");

            migrationBuilder.DropForeignKey(
                name: "FK_knowledge_candidate_memory_candidate_SourceMemoryCandidateId",
                table: "knowledge_candidate");

            migrationBuilder.DropTable(
                name: "memory_audit");

            migrationBuilder.DropTable(
                name: "memory_entry");

            migrationBuilder.DropTable(
                name: "memory_observation");

            migrationBuilder.DropTable(
                name: "memory_candidate");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_candidate_SourceConversationMessageId",
                table: "knowledge_candidate");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_candidate_SourceMemoryCandidateId",
                table: "knowledge_candidate");

            migrationBuilder.DropColumn(
                name: "SourceConversationMessageId",
                table: "knowledge_candidate");

            migrationBuilder.DropColumn(
                name: "SourceMemoryCandidateId",
                table: "knowledge_candidate");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "knowledge_candidate");

            migrationBuilder.AlterColumn<Guid>(
                name: "HandoffCaseId",
                table: "knowledge_candidate",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true);
        }
    }
}
