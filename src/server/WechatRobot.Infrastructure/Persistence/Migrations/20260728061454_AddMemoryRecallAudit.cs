using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryRecallAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MemoryRecallJson",
                table: "retrieval_audit",
                type: "json",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE `retrieval_audit` SET `MemoryRecallJson` = JSON_ARRAY() WHERE `MemoryRecallJson` IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "MemoryRecallJson",
                table: "retrieval_audit",
                type: "json",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "json",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MemoryRecallJson",
                table: "retrieval_audit");
        }
    }
}
