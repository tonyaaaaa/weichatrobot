using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableGroupOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "worktool_operation_audit",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAtUtc",
                table: "worktool_operation_audit",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedCommandJson",
                table: "worktool_operation_audit",
                type: "varchar(8192)",
                maxLength: 8192,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExternalDispatchStartedAtUtc",
                table: "worktool_operation_audit",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAtUtc",
                table: "worktool_operation_audit",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "worktool_operation_audit",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RobotConfigId",
                table: "worktool_operation_audit",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "worktool_operation_audit",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_worktool_operation_audit_RobotConfigId",
                table: "worktool_operation_audit",
                column: "RobotConfigId");

            migrationBuilder.CreateIndex(
                name: "IX_worktool_operation_audit_Status_CreatedAtUtc",
                table: "worktool_operation_audit",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_worktool_operation_audit_robot_config_RobotConfigId",
                table: "worktool_operation_audit",
                column: "RobotConfigId",
                principalTable: "robot_config",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_worktool_operation_audit_robot_config_RobotConfigId",
                table: "worktool_operation_audit");

            migrationBuilder.DropIndex(
                name: "IX_worktool_operation_audit_RobotConfigId",
                table: "worktool_operation_audit");

            migrationBuilder.DropIndex(
                name: "IX_worktool_operation_audit_Status_CreatedAtUtc",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "EncryptedCommandJson",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "ExternalDispatchStartedAtUtc",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtc",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "RobotConfigId",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "worktool_operation_audit");
        }
    }
}
