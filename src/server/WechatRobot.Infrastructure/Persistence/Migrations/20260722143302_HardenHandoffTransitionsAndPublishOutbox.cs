using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenHandoffTransitionsAndPublishOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ReviewerUserId",
                table: "knowledge_review",
                type: "char(36)",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "char(36)");

            migrationBuilder.Sql("UPDATE handoff_case h LEFT JOIN AspNetUsers u ON h.AssigneeUserId = u.Id SET h.AssigneeUserId = NULL WHERE h.AssigneeUserId IS NOT NULL AND u.Id IS NULL;");
            migrationBuilder.Sql("UPDATE handoff_case h LEFT JOIN AspNetUsers u ON h.ResolvedByUserId = u.Id SET h.ResolvedByUserId = NULL WHERE h.ResolvedByUserId IS NOT NULL AND u.Id IS NULL;");
            migrationBuilder.Sql("UPDATE handoff_message m LEFT JOIN AspNetUsers u ON m.AuthenticatedUserId = u.Id SET m.AuthenticatedUserId = NULL WHERE m.AuthenticatedUserId IS NOT NULL AND u.Id IS NULL;");
            migrationBuilder.Sql("UPDATE knowledge_review r LEFT JOIN AspNetUsers u ON r.ReviewerUserId = u.Id SET r.ReviewerUserId = NULL WHERE r.ReviewerUserId IS NOT NULL AND u.Id IS NULL;");

            migrationBuilder.CreateTable(
                name: "handoff_transition",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    HandoffCaseId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    FromState = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    ToState = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_handoff_transition", x => x.Id);
                    table.ForeignKey(
                        name: "FK_handoff_transition_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_handoff_transition_handoff_case_HandoffCaseId",
                        column: x => x.HandoffCaseId,
                        principalTable: "handoff_case",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_review_ReviewerUserId",
                table: "knowledge_review",
                column: "ReviewerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_handoff_message_AuthenticatedUserId",
                table: "handoff_message",
                column: "AuthenticatedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_handoff_case_AssigneeUserId",
                table: "handoff_case",
                column: "AssigneeUserId");

            migrationBuilder.CreateIndex(
                name: "IX_handoff_case_ResolvedByUserId",
                table: "handoff_case",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_handoff_transition_ActorUserId",
                table: "handoff_transition",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_handoff_transition_HandoffCaseId_Sequence",
                table: "handoff_transition",
                columns: new[] { "HandoffCaseId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_handoff_transition_IdempotencyKey",
                table: "handoff_transition",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_handoff_case_AspNetUsers_AssigneeUserId",
                table: "handoff_case",
                column: "AssigneeUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_handoff_case_AspNetUsers_ResolvedByUserId",
                table: "handoff_case",
                column: "ResolvedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_handoff_message_AspNetUsers_AuthenticatedUserId",
                table: "handoff_message",
                column: "AuthenticatedUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_knowledge_review_AspNetUsers_ReviewerUserId",
                table: "knowledge_review",
                column: "ReviewerUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_handoff_case_AspNetUsers_AssigneeUserId",
                table: "handoff_case");

            migrationBuilder.DropForeignKey(
                name: "FK_handoff_case_AspNetUsers_ResolvedByUserId",
                table: "handoff_case");

            migrationBuilder.DropForeignKey(
                name: "FK_handoff_message_AspNetUsers_AuthenticatedUserId",
                table: "handoff_message");

            migrationBuilder.DropForeignKey(
                name: "FK_knowledge_review_AspNetUsers_ReviewerUserId",
                table: "knowledge_review");

            migrationBuilder.DropTable(
                name: "handoff_transition");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_review_ReviewerUserId",
                table: "knowledge_review");

            migrationBuilder.DropIndex(
                name: "IX_handoff_message_AuthenticatedUserId",
                table: "handoff_message");

            migrationBuilder.DropIndex(
                name: "IX_handoff_case_AssigneeUserId",
                table: "handoff_case");

            migrationBuilder.Sql("UPDATE knowledge_review SET ReviewerUserId = '00000000-0000-0000-0000-000000000000' WHERE ReviewerUserId IS NULL;");

            migrationBuilder.DropIndex(
                name: "IX_handoff_case_ResolvedByUserId",
                table: "handoff_case");

            migrationBuilder.AlterColumn<Guid>(
                name: "ReviewerUserId",
                table: "knowledge_review",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true);
        }
    }
}
