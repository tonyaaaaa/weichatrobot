using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableRobotSendCoordination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SendCoordinationVersion",
                table: "robot_config",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SendLeaseExpiresAtUtc",
                table: "robot_config",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SendLeaseOwner",
                table: "robot_config",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SendRateTokens",
                table: "robot_config",
                type: "decimal(10,4)",
                precision: 10,
                scale: 4,
                nullable: false,
                defaultValue: 50m);

            migrationBuilder.AddColumn<DateTime>(
                name: "SendRateUpdatedAtUtc",
                table: "robot_config",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SendCoordinationVersion",
                table: "robot_config");

            migrationBuilder.DropColumn(
                name: "SendLeaseExpiresAtUtc",
                table: "robot_config");

            migrationBuilder.DropColumn(
                name: "SendLeaseOwner",
                table: "robot_config");

            migrationBuilder.DropColumn(
                name: "SendRateTokens",
                table: "robot_config");

            migrationBuilder.DropColumn(
                name: "SendRateUpdatedAtUtc",
                table: "robot_config");
        }
    }
}
