using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableWorkerLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_durable_job_Status_AvailableAtUtc",
                table: "durable_job");

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "send_command",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAtUtc",
                table: "send_command",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAtUtc",
                table: "send_command",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "send_command",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAtUtc",
                table: "send_command",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "send_command",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SendRateLimitPerMinute",
                table: "robot_config",
                type: "int",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAtUtc",
                table: "durable_job",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "durable_job",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAtUtc",
                table: "durable_job",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "durable_job",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "SendCommandId",
                table: "dead_letter",
                type: "char(36)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_send_command_Status_NextAttemptAtUtc",
                table: "send_command",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_robot_config_send_rate_limit",
                table: "robot_config",
                sql: "`SendRateLimitPerMinute` BETWEEN 1 AND 60");

            migrationBuilder.CreateIndex(
                name: "IX_durable_job_Status_NextAttemptAtUtc",
                table: "durable_job",
                columns: new[] { "Status", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_send_command_Status_NextAttemptAtUtc",
                table: "send_command");

            migrationBuilder.DropCheckConstraint(
                name: "CK_robot_config_send_rate_limit",
                table: "robot_config");

            migrationBuilder.DropIndex(
                name: "IX_durable_job_Status_NextAttemptAtUtc",
                table: "durable_job");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "send_command");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "send_command");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtc",
                table: "send_command");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "send_command");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtc",
                table: "send_command");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "send_command");

            migrationBuilder.DropColumn(
                name: "SendRateLimitPerMinute",
                table: "robot_config");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "durable_job");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "durable_job");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtc",
                table: "durable_job");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "durable_job");

            migrationBuilder.DropColumn(
                name: "SendCommandId",
                table: "dead_letter");

            migrationBuilder.CreateIndex(
                name: "IX_durable_job_Status_AvailableAtUtc",
                table: "durable_job",
                columns: new[] { "Status", "AvailableAtUtc" });
        }
    }
}
