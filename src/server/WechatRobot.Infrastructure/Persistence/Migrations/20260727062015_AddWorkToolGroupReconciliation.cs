using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkToolGroupReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReconciledGroupProfileId",
                table: "worktool_operation_audit",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReconciliationAttemptCount",
                table: "worktool_operation_audit",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReconciliationNextAttemptAtUtc",
                table: "worktool_operation_audit",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReconciliationStatus",
                table: "worktool_operation_audit",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_worktool_operation_audit_ReconciledGroupProfileId",
                table: "worktool_operation_audit",
                column: "ReconciledGroupProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_worktool_operation_audit_reconciliation_due",
                table: "worktool_operation_audit",
                columns: new[] { "ReconciliationStatus", "ReconciliationNextAttemptAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_worktool_operation_audit_reconciled_group",
                table: "worktool_operation_audit",
                column: "ReconciledGroupProfileId",
                principalTable: "group_profile",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_worktool_operation_audit_reconciled_group",
                table: "worktool_operation_audit");

            migrationBuilder.DropIndex(
                name: "IX_worktool_operation_audit_ReconciledGroupProfileId",
                table: "worktool_operation_audit");

            migrationBuilder.DropIndex(
                name: "IX_worktool_operation_audit_reconciliation_due",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "ReconciledGroupProfileId",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "ReconciliationAttemptCount",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "ReconciliationNextAttemptAtUtc",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "ReconciliationStatus",
                table: "worktool_operation_audit");
        }
    }
}
