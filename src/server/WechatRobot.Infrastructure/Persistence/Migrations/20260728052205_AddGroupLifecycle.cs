using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAtUtc",
                table: "group_profile",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StateVersion",
                table: "group_profile",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "GroupProfileId",
                table: "durable_job",
                type: "char(36)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_job_GroupProfileId",
                table: "durable_job",
                column: "GroupProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_durable_job_GroupProfileId",
                table: "durable_job");

            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "StateVersion",
                table: "group_profile");

            migrationBuilder.DropColumn(
                name: "GroupProfileId",
                table: "durable_job");
        }
    }
}
