using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboundPolicyAndHandoffPause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConfigurationVersion",
                table: "group_profile",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "HandoffPausePolicy",
                table: "group_profile",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Group");

            migrationBuilder.AddColumn<string>(
                name: "TerminalDecision",
                table: "conversation_message",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TerminalEvidenceJson",
                table: "conversation_message",
                type: "json",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TerminalReason",
                table: "conversation_message",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_group_profile_handoff_pause_policy",
                table: "group_profile",
                sql: "`HandoffPausePolicy` IN ('Group','Sender')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_group_profile_handoff_pause_policy",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "ConfigurationVersion",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "HandoffPausePolicy",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "TerminalDecision",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "TerminalEvidenceJson",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "TerminalReason",
                table: "conversation_message");
        }
    }
}
