using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEncryptedModelProviderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseUrl",
                table: "model_config",
                type: "varchar(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ConfigurationType",
                table: "model_config",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EncryptedApiKey",
                table: "model_config",
                type: "longtext",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRetries",
                table: "model_config",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TimeoutSeconds",
                table: "model_config",
                type: "int",
                nullable: false,
                defaultValue: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaseUrl",
                table: "model_config");

            migrationBuilder.DropColumn(
                name: "ConfigurationType",
                table: "model_config");

            migrationBuilder.DropColumn(
                name: "EncryptedApiKey",
                table: "model_config");

            migrationBuilder.DropColumn(
                name: "MaxRetries",
                table: "model_config");

            migrationBuilder.DropColumn(
                name: "TimeoutSeconds",
                table: "model_config");
        }
    }
}
