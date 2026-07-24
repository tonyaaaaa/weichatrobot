using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenModelConfigurationManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_model_config_Name",
                table: "model_config");

            migrationBuilder.AddColumn<Guid>(
                name: "ModelConfigurationId",
                table: "retrieval_audit",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApiKeyVersion",
                table: "model_config",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ConnectionStatus",
                table: "model_config",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Untested");

            migrationBuilder.AddColumn<string>(
                name: "LastTestFailureSummary",
                table: "model_config",
                type: "varchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastTestedAtUtc",
                table: "model_config",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "model_config",
                type: "varchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TestedConfigurationFingerprint",
                table: "model_config",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "model_config",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DefaultConfigurationType",
                table: "model_config",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                computedColumnSql: "CASE WHEN `IsDefault` = 1 THEN `ConfigurationType` ELSE NULL END",
                stored: true);

            migrationBuilder.Sql("""
                UPDATE model_config
                SET NormalizedName = UPPER(TRIM(Name)),
                    ConnectionStatus = 'Untested';
                """);

            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS validate_model_configuration_migration;");
            migrationBuilder.Sql("""
                CREATE PROCEDURE validate_model_configuration_migration()
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM model_config
                        GROUP BY NormalizedName
                        HAVING COUNT(*) > 1
                    ) THEN
                        SIGNAL SQLSTATE '45000'
                            SET MESSAGE_TEXT = 'Duplicate normalized model configuration names exist.';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM model_config
                        WHERE IsDefault = 1
                        GROUP BY ConfigurationType
                        HAVING COUNT(*) > 1
                    ) THEN
                        SIGNAL SQLSTATE '45000'
                            SET MESSAGE_TEXT = 'Multiple default model configurations exist for one type.';
                    END IF;
                END;
                """);
            migrationBuilder.Sql("CALL validate_model_configuration_migration();");
            migrationBuilder.Sql("DROP PROCEDURE validate_model_configuration_migration;");

            migrationBuilder.Sql("""
                UPDATE retrieval_audit AS audit
                INNER JOIN model_config AS config
                    ON config.Id = JSON_UNQUOTE(
                        JSON_EXTRACT(audit.InputSummaryJson, '$.ModelConfigurationId'))
                SET audit.ModelConfigurationId = config.Id
                WHERE JSON_EXTRACT(
                    audit.InputSummaryJson,
                    '$.ModelConfigurationId') IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_retrieval_audit_ModelConfigurationId",
                table: "retrieval_audit",
                column: "ModelConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_model_config_DefaultConfigurationType",
                table: "model_config",
                column: "DefaultConfigurationType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_config_NormalizedName",
                table: "model_config",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_retrieval_audit_model_config_ModelConfigurationId",
                table: "retrieval_audit",
                column: "ModelConfigurationId",
                principalTable: "model_config",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_retrieval_audit_model_config_ModelConfigurationId",
                table: "retrieval_audit");

            migrationBuilder.DropIndex(
                name: "IX_retrieval_audit_ModelConfigurationId",
                table: "retrieval_audit");

            migrationBuilder.DropIndex(
                name: "IX_model_config_DefaultConfigurationType",
                table: "model_config");

            migrationBuilder.DropIndex(
                name: "IX_model_config_NormalizedName",
                table: "model_config");

            migrationBuilder.DropColumn(
                name: "DefaultConfigurationType",
                table: "model_config");

            migrationBuilder.DropColumn(
                name: "ModelConfigurationId",
                table: "retrieval_audit");

            migrationBuilder.DropColumn(
                name: "ApiKeyVersion",
                table: "model_config");

            migrationBuilder.DropColumn(
                name: "ConnectionStatus",
                table: "model_config");

            migrationBuilder.DropColumn(
                name: "LastTestFailureSummary",
                table: "model_config");

            migrationBuilder.DropColumn(
                name: "LastTestedAtUtc",
                table: "model_config");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "model_config");

            migrationBuilder.DropColumn(
                name: "TestedConfigurationFingerprint",
                table: "model_config");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "model_config");

            migrationBuilder.CreateIndex(
                name: "IX_model_config_Name",
                table: "model_config",
                column: "Name",
                unique: true);
        }
    }
}
