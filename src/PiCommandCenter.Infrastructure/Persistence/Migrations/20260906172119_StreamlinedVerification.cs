using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiCommandCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StreamlinedVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AttemptId",
                table: "VerificationRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "VerificationRuns",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PolicyRevision",
                table: "VerificationRuns",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RunKind",
                table: "VerificationRuns",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Intermediate");

            migrationBuilder.AddColumn<string>(
                name: "TrustedVerificationProfileId",
                table: "Projects",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrustedVerificationProfileRevision",
                table: "Projects",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaselineVersion",
                table: "ExecutionAssignments",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MandatoryCommandIdsJson",
                table: "ExecutionAssignments",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrustedVerificationProfileId",
                table: "ExecutionAssignments",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrustedVerificationProfileRevision",
                table: "ExecutionAssignments",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationPolicyRevision",
                table: "ExecutionAssignments",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VerificationPolicyUpgradeAudits",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProfileRevision = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MigratedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationPolicyUpgradeAudits", x => x.ProjectId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VerificationRuns_FinalIdentity",
                table: "VerificationRuns",
                columns: new[] { "RequestId", "Fingerprint", "PolicyRevision", "ProfileId", "CommandId", "RunKind" },
                unique: true,
                filter: "\"RunKind\" <> 'Intermediate'");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationPolicyUpgradeAudits_ProjectId",
                table: "VerificationPolicyUpgradeAudits",
                column: "ProjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VerificationPolicyUpgradeAudits");

            migrationBuilder.DropIndex(
                name: "IX_VerificationRuns_FinalIdentity",
                table: "VerificationRuns");

            migrationBuilder.DropColumn(
                name: "AttemptId",
                table: "VerificationRuns");

            migrationBuilder.DropColumn(
                name: "Fingerprint",
                table: "VerificationRuns");

            migrationBuilder.DropColumn(
                name: "PolicyRevision",
                table: "VerificationRuns");

            migrationBuilder.DropColumn(
                name: "RunKind",
                table: "VerificationRuns");

            migrationBuilder.DropColumn(
                name: "TrustedVerificationProfileId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "TrustedVerificationProfileRevision",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "BaselineVersion",
                table: "ExecutionAssignments");

            migrationBuilder.DropColumn(
                name: "MandatoryCommandIdsJson",
                table: "ExecutionAssignments");

            migrationBuilder.DropColumn(
                name: "TrustedVerificationProfileId",
                table: "ExecutionAssignments");

            migrationBuilder.DropColumn(
                name: "TrustedVerificationProfileRevision",
                table: "ExecutionAssignments");

            migrationBuilder.DropColumn(
                name: "VerificationPolicyRevision",
                table: "ExecutionAssignments");
        }
    }
}
