using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEncryptedRobotCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CallbackRouteCode",
                table: "robot_config",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedWorkToolRobotId",
                table: "robot_config",
                type: "varchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_robot_config_CallbackRouteCode",
                table: "robot_config",
                column: "CallbackRouteCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_robot_config_CallbackRouteCode",
                table: "robot_config");

            migrationBuilder.DropColumn(
                name: "CallbackRouteCode",
                table: "robot_config");

            migrationBuilder.DropColumn(
                name: "EncryptedWorkToolRobotId",
                table: "robot_config");
        }
    }
}
