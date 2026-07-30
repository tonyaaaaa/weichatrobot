using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivateKnowledgeIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SystemKind",
                table: "knowledge_tag",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChangeKind",
                table: "knowledge_document_version",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "New");

            migrationBuilder.AddColumn<string>(
                name: "SourceActorDisplayName",
                table: "knowledge_document_version",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceBatchId",
                table: "knowledge_document_version",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceConversationMessageId",
                table: "knowledge_document_version",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: "knowledge_document_version",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "LegacyUnknown");

            migrationBuilder.AddColumn<Guid>(
                name: "SupersedesVersionId",
                table: "knowledge_document_version",
                type: "char(36)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "private_knowledge_ingest_batch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    RobotConfigId = table.Column<Guid>(type: "char(36)", nullable: false),
                    SourceConversationMessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    RoomType = table.Column<int>(type: "int", nullable: false),
                    SourceActorDisplayName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    ModelConfigurationId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ModelConfigurationVersion = table.Column<int>(type: "int", nullable: true),
                    TotalCount = table.Column<int>(type: "int", nullable: false),
                    NewCount = table.Column<int>(type: "int", nullable: false),
                    DuplicateCount = table.Column<int>(type: "int", nullable: false),
                    SupplementCount = table.Column<int>(type: "int", nullable: false),
                    CorrectionCount = table.Column<int>(type: "int", nullable: false),
                    FailureCode = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    ReceivedNotificationState = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    FinalNotificationState = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_private_knowledge_ingest_batch", x => x.Id);
                    table.ForeignKey(
                        name: "FK_private_knowledge_ingest_batch_conversation_message_SourceCo~",
                        column: x => x.SourceConversationMessageId,
                        principalTable: "conversation_message",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_private_knowledge_ingest_batch_model_config_ModelConfigurati~",
                        column: x => x.ModelConfigurationId,
                        principalTable: "model_config",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_private_knowledge_ingest_batch_robot_config_RobotConfigId",
                        column: x => x.RobotConfigId,
                        principalTable: "robot_config",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "private_knowledge_ingest_item",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    BatchId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Question = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false),
                    Answer = table.Column<string>(type: "longtext", nullable: false),
                    ChangeKind = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    MatchedDocumentId = table.Column<Guid>(type: "char(36)", nullable: true),
                    MatchedVersionId = table.Column<Guid>(type: "char(36)", nullable: true),
                    StagedDocumentId = table.Column<Guid>(type: "char(36)", nullable: true),
                    StagedVersionId = table.Column<Guid>(type: "char(36)", nullable: true),
                    QuestionFingerprint = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    AnswerFingerprint = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    ProposedTagsJson = table.Column<string>(type: "json", nullable: false),
                    ResolvedTagIdsJson = table.Column<string>(type: "json", nullable: false),
                    FailureCode = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_private_knowledge_ingest_item", x => x.Id);
                    table.ForeignKey(
                        name: "FK_private_knowledge_ingest_item_private_knowledge_ingest_batch~",
                        column: x => x.BatchId,
                        principalTable: "private_knowledge_ingest_batch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_tag_SystemKind",
                table: "knowledge_tag",
                column: "SystemKind",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_version_SourceConversationMessageId",
                table: "knowledge_document_version",
                column: "SourceConversationMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_document_version_SupersedesVersionId",
                table: "knowledge_document_version",
                column: "SupersedesVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_private_knowledge_ingest_batch_ModelConfigurationId",
                table: "private_knowledge_ingest_batch",
                column: "ModelConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_private_knowledge_ingest_batch_RobotConfigId",
                table: "private_knowledge_ingest_batch",
                column: "RobotConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_private_knowledge_ingest_batch_SourceConversationMessageId",
                table: "private_knowledge_ingest_batch",
                column: "SourceConversationMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_private_knowledge_ingest_batch_Status_UpdatedAtUtc",
                table: "private_knowledge_ingest_batch",
                columns: new[] { "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_private_knowledge_ingest_item_BatchId_Sequence",
                table: "private_knowledge_ingest_item",
                columns: new[] { "BatchId", "Sequence" },
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO knowledge_tag
                    (Id, Name, NormalizedName, IsEnabled, IsGlobalPublic, Version, CreatedAtUtc, SystemKind)
                SELECT
                    'f5b8e5c1-5f2d-4d61-9ae0-126dca90a0e1',
                    '全局知识',
                    'SYSTEM:GLOBAL_KNOWLEDGE',
                    1,
                    1,
                    0,
                    UTC_TIMESTAMP(6),
                    'GlobalKnowledge'
                FROM DUAL
                WHERE NOT EXISTS (
                    SELECT 1 FROM knowledge_tag WHERE SystemKind = 'GlobalKnowledge'
                );
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_knowledge_document_version_knowledge_document_version_Supers~",
                table: "knowledge_document_version",
                column: "SupersedesVersionId",
                principalTable: "knowledge_document_version",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_knowledge_document_version_knowledge_document_version_Supers~",
                table: "knowledge_document_version");

            migrationBuilder.DropTable(
                name: "private_knowledge_ingest_item");

            migrationBuilder.DropTable(
                name: "private_knowledge_ingest_batch");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_tag_SystemKind",
                table: "knowledge_tag");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_document_version_SourceConversationMessageId",
                table: "knowledge_document_version");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_document_version_SupersedesVersionId",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "SystemKind",
                table: "knowledge_tag");

            migrationBuilder.DropColumn(
                name: "ChangeKind",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "SourceActorDisplayName",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "SourceBatchId",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "SourceConversationMessageId",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                table: "knowledge_document_version");

            migrationBuilder.DropColumn(
                name: "SupersedesVersionId",
                table: "knowledge_document_version");
        }
    }
}
