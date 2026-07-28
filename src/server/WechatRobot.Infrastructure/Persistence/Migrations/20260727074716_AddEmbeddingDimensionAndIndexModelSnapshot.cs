using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingDimensionAndIndexModelSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmbeddingDimension",
                table: "model_config",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ModelConfigurationId",
                table: "knowledge_index_job",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ModelConfigurationVersion",
                table: "knowledge_index_job",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmbeddingDimension",
                table: "model_config");

            migrationBuilder.DropColumn(
                name: "ModelConfigurationId",
                table: "knowledge_index_job");

            migrationBuilder.DropColumn(
                name: "ModelConfigurationVersion",
                table: "knowledge_index_job");
        }
    }
}
