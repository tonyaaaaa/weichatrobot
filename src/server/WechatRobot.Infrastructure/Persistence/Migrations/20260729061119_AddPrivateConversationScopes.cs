using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivateConversationScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChannelType",
                table: "conversation_message",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Group");

            migrationBuilder.AddColumn<string>(
                name: "PeerDisplayName",
                table: "conversation_message",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoomType",
                table: "conversation_message",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScopeHash",
                table: "conversation_message",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversation_message_RobotConfigId_RoomType_ScopeHash",
                table: "conversation_message",
                columns: new[] { "RobotConfigId", "RoomType", "ScopeHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_conversation_message_RobotConfigId_RoomType_ScopeHash",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "ChannelType",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "PeerDisplayName",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "RoomType",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "ScopeHash",
                table: "conversation_message");

        }
    }
}
