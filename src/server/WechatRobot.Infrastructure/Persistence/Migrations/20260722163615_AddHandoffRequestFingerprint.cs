using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHandoffRequestFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestFingerprint",
                table: "handoff_case",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartIdempotencyKey",
                table: "handoff_case",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_handoff_case_StartIdempotencyKey",
                table: "handoff_case",
                column: "StartIdempotencyKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_handoff_case_StartIdempotencyKey",
                table: "handoff_case");

            migrationBuilder.DropColumn(
                name: "RequestFingerprint",
                table: "handoff_case");

            migrationBuilder.DropColumn(
                name: "StartIdempotencyKey",
                table: "handoff_case");
        }
    }
}
