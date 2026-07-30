using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivateConversationSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "GroupProfileId",
                table: "conversation_session",
                type: "char(36)",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "char(36)");

            migrationBuilder.AddColumn<string>(
                name: "ChannelType",
                table: "conversation_session",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Group");

            migrationBuilder.AddColumn<string>(
                name: "PeerDisplayName",
                table: "conversation_session",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RobotConfigId",
                table: "conversation_session",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoomType",
                table: "conversation_session",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScopeHash",
                table: "conversation_session",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversation_session_RobotConfigId_RoomType_ScopeHash",
                table: "conversation_session",
                columns: new[] { "RobotConfigId", "RoomType", "ScopeHash" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_conversation_session_robot_config_RobotConfigId",
                table: "conversation_session",
                column: "RobotConfigId",
                principalTable: "robot_config",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_conversation_session_robot_config_RobotConfigId",
                table: "conversation_session");

            migrationBuilder.DropIndex(
                name: "IX_conversation_session_RobotConfigId_RoomType_ScopeHash",
                table: "conversation_session");

            migrationBuilder.DropColumn(
                name: "ChannelType",
                table: "conversation_session");

            migrationBuilder.DropColumn(
                name: "PeerDisplayName",
                table: "conversation_session");

            migrationBuilder.DropColumn(
                name: "RobotConfigId",
                table: "conversation_session");

            migrationBuilder.DropColumn(
                name: "RoomType",
                table: "conversation_session");

            migrationBuilder.DropColumn(
                name: "ScopeHash",
                table: "conversation_session");

            migrationBuilder.AlterColumn<Guid>(
                name: "GroupProfileId",
                table: "conversation_session",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true);
        }
    }
}
