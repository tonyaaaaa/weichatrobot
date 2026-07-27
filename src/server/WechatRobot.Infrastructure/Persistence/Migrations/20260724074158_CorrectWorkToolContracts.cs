using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CorrectWorkToolContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AcceptedAtUtc",
                table: "worktool_operation_audit",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkToolCommandMessageId",
                table: "worktool_operation_audit",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkToolFailListJson",
                table: "worktool_operation_audit",
                type: "json",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WorkToolResultAtUtc",
                table: "worktool_operation_audit",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkToolResultCode",
                table: "worktool_operation_audit",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkToolSuccessListJson",
                table: "worktool_operation_audit",
                type: "json",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AcceptedAtUtc",
                table: "send_command",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkToolCommandMessageId",
                table: "send_command",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkToolFailListJson",
                table: "send_command",
                type: "json",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WorkToolResultAtUtc",
                table: "send_command",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkToolResultCode",
                table: "send_command",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkToolSuccessListJson",
                table: "send_command",
                type: "json",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedCallbackSecret",
                table: "robot_config",
                type: "varchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PreviousCallbackSecretExpiresAtUtc",
                table: "robot_config",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousCallbackSecretHash",
                table: "robot_config",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ExternalGroupId",
                table: "group_profile",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AddColumn<string>(
                name: "WorkToolGroupRemark",
                table: "group_profile",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupRemark",
                table: "conversation_message",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE worktool_operation_audit
                SET AcceptedAtUtc = CASE
                        WHEN Status = 'Succeeded' THEN ExternalDispatchStartedAtUtc
                        ELSE AcceptedAtUtc
                    END,
                    CompletedAtUtc = CASE
                        WHEN Status = 'Succeeded' THEN NULL
                        ELSE CompletedAtUtc
                    END,
                    Status = CASE Status
                        WHEN 'Queued' THEN 'queued'
                        WHEN 'ExternalInFlight' THEN 'dispatching'
                        WHEN 'Succeeded' THEN 'accepted'
                        WHEN 'Failed' THEN 'rejected'
                        WHEN 'DeliveryUncertain' THEN 'deliveryUnknown'
                        ELSE Status
                    END;
                """);

            migrationBuilder.Sql(
                """
                UPDATE send_command
                SET AcceptedAtUtc = CASE
                        WHEN Status = 'completed' THEN SentAtUtc
                        ELSE AcceptedAtUtc
                    END,
                    CompletedAtUtc = CASE
                        WHEN Status = 'completed' THEN NULL
                        ELSE CompletedAtUtc
                    END,
                    Status = CASE Status
                        WHEN 'completed' THEN 'accepted'
                        WHEN 'externalInFlight' THEN 'dispatching'
                        WHEN 'deliveryUncertain' THEN 'deliveryUnknown'
                        ELSE Status
                    END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_worktool_operation_audit_WorkToolCommandMessageId",
                table: "worktool_operation_audit",
                column: "WorkToolCommandMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_send_command_WorkToolCommandMessageId",
                table: "send_command",
                column: "WorkToolCommandMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_group_profile_RobotConfigId_Name_WorkToolGroupRemark",
                table: "group_profile",
                columns: new[] { "RobotConfigId", "Name", "WorkToolGroupRemark" });

            migrationBuilder.DropIndex(
                name: "IX_group_profile_RobotConfigId_ExternalGroupId",
                table: "group_profile");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE worktool_operation_audit
                SET Status = CASE Status
                    WHEN 'queued' THEN 'Queued'
                    WHEN 'dispatching' THEN 'ExternalInFlight'
                    WHEN 'accepted' THEN 'Succeeded'
                    WHEN 'rejected' THEN 'Failed'
                    WHEN 'deliveryUnknown' THEN 'DeliveryUncertain'
                    ELSE Status
                END;
                """);

            migrationBuilder.Sql(
                """
                UPDATE send_command
                SET CompletedAtUtc = CASE
                        WHEN Status = 'accepted' THEN COALESCE(AcceptedAtUtc, SentAtUtc)
                        ELSE CompletedAtUtc
                    END,
                    Status = CASE Status
                        WHEN 'accepted' THEN 'completed'
                        WHEN 'dispatching' THEN 'externalInFlight'
                        WHEN 'deliveryUnknown' THEN 'deliveryUncertain'
                        ELSE Status
                    END;
                """);

            migrationBuilder.DropIndex(
                name: "IX_worktool_operation_audit_WorkToolCommandMessageId",
                table: "worktool_operation_audit");

            migrationBuilder.DropIndex(
                name: "IX_send_command_WorkToolCommandMessageId",
                table: "send_command");

            migrationBuilder.CreateIndex(
                name: "IX_group_profile_RobotConfigId_ExternalGroupId",
                table: "group_profile",
                columns: new[] { "RobotConfigId", "ExternalGroupId" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_group_profile_RobotConfigId_Name_WorkToolGroupRemark",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "AcceptedAtUtc",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "WorkToolCommandMessageId",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "WorkToolFailListJson",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "WorkToolResultAtUtc",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "WorkToolResultCode",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "WorkToolSuccessListJson",
                table: "worktool_operation_audit");

            migrationBuilder.DropColumn(
                name: "AcceptedAtUtc",
                table: "send_command");

            migrationBuilder.DropColumn(
                name: "WorkToolCommandMessageId",
                table: "send_command");

            migrationBuilder.DropColumn(
                name: "WorkToolFailListJson",
                table: "send_command");

            migrationBuilder.DropColumn(
                name: "WorkToolResultAtUtc",
                table: "send_command");

            migrationBuilder.DropColumn(
                name: "WorkToolResultCode",
                table: "send_command");

            migrationBuilder.DropColumn(
                name: "WorkToolSuccessListJson",
                table: "send_command");

            migrationBuilder.DropColumn(
                name: "EncryptedCallbackSecret",
                table: "robot_config");

            migrationBuilder.DropColumn(
                name: "PreviousCallbackSecretExpiresAtUtc",
                table: "robot_config");

            migrationBuilder.DropColumn(
                name: "PreviousCallbackSecretHash",
                table: "robot_config");

            migrationBuilder.DropColumn(
                name: "WorkToolGroupRemark",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "GroupRemark",
                table: "conversation_message");

            migrationBuilder.AlterColumn<string>(
                name: "ExternalGroupId",
                table: "group_profile",
                type: "varchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "varchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

        }
    }
}
