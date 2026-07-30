using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WechatRobot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFixedReplyTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fixed_reply_template",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    NormalizedName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    IntentDescription = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false),
                    ReplyText = table.Column<string>(type: "text", nullable: false),
                    ScopeType = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "char(36)", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fixed_reply_template", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fixed_reply_template_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_fixed_reply_template_AspNetUsers_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "fixed_reply_template_example",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TemplateId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ExampleText = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    NormalizedText = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fixed_reply_template_example", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fixed_reply_template_example_fixed_reply_template_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "fixed_reply_template",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "fixed_reply_template_group_rule",
                columns: table => new
                {
                    TemplateId = table.Column<Guid>(type: "char(36)", nullable: false),
                    GroupProfileId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Effect = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "char(36)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fixed_reply_template_group_rule", x => new { x.TemplateId, x.GroupProfileId });
                    table.ForeignKey(
                        name: "FK_fixed_reply_template_group_rule_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_fixed_reply_template_group_rule_fixed_reply_template_Templat~",
                        column: x => x.TemplateId,
                        principalTable: "fixed_reply_template",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_fixed_reply_template_group_rule_group_profile_GroupProfileId",
                        column: x => x.GroupProfileId,
                        principalTable: "group_profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_fixed_reply_template_CreatedByUserId",
                table: "fixed_reply_template",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_fixed_reply_template_IsEnabled_DeletedAtUtc_Priority",
                table: "fixed_reply_template",
                columns: new[] { "IsEnabled", "DeletedAtUtc", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_fixed_reply_template_NormalizedName",
                table: "fixed_reply_template",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fixed_reply_template_UpdatedByUserId",
                table: "fixed_reply_template",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_fixed_reply_template_example_TemplateId_NormalizedText",
                table: "fixed_reply_template_example",
                columns: new[] { "TemplateId", "NormalizedText" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fixed_reply_template_group_rule_CreatedByUserId",
                table: "fixed_reply_template_group_rule",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_fixed_reply_template_group_rule_GroupProfileId_Effect",
                table: "fixed_reply_template_group_rule",
                columns: new[] { "GroupProfileId", "Effect" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fixed_reply_template_example");

            migrationBuilder.DropTable(
                name: "fixed_reply_template_group_rule");

            migrationBuilder.DropTable(
                name: "fixed_reply_template");
        }
    }
}
