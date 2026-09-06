using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiCommandCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FleetOwnedProjectsCutover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkspaceBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepositoryPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CanonicalRepositoryPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ValidationRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ValidationCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ValidationDetail = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ValidatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceBindings_FleetNodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "FleetNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspaceBindings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionAssignments",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceBindingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeIdSnapshot = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalRepositoryPathSnapshot = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    DefaultBranchSnapshot = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BindingValidationRevisionSnapshot = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ClaimToken = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AssignedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LeaseExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRenewedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastReconciledAt = table.Column<long>(type: "INTEGER", nullable: true),
                    TerminalAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionAssignments", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_ExecutionAssignments_FleetNodes_NodeIdSnapshot",
                        column: x => x.NodeIdSnapshot,
                        principalTable: "FleetNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExecutionAssignments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceBindings_ProjectId",
                table: "WorkspaceBindings",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceBindings_NodeId_RepositoryPath",
                table: "WorkspaceBindings",
                columns: new[] { "NodeId", "RepositoryPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceBindings_NodeId_CanonicalRepositoryPath",
                table: "WorkspaceBindings",
                columns: new[] { "NodeId", "CanonicalRepositoryPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionAssignments_NodeIdSnapshot_State",
                table: "ExecutionAssignments",
                columns: new[] { "NodeIdSnapshot", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionAssignments_ProjectId_State",
                table: "ExecutionAssignments",
                columns: new[] { "ProjectId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionAssignments_WorkspaceBindingId",
                table: "ExecutionAssignments",
                column: "WorkspaceBindingId");

            migrationBuilder.Sql(
                """
                INSERT INTO "WorkspaceBindings" (
                    "Id", "ProjectId", "NodeId", "RepositoryPath", "CanonicalRepositoryPath",
                    "Status", "ValidationRevision", "ValidationCode", "ValidationDetail", "ValidatedAt",
                    "CreatedAt", "UpdatedAt", "Version")
                SELECT
                    "Id", "Id", "NodeId", "RepositoryPath", NULL,
                    'PendingValidation', 1, NULL, NULL, NULL,
                    "CreatedAt", "UpdatedAt", 1
                FROM "Projects";
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO "ExecutionAssignments" (
                    "RequestId", "ProjectId", "WorkspaceBindingId", "NodeIdSnapshot",
                    "CanonicalRepositoryPathSnapshot", "DefaultBranchSnapshot",
                    "BindingValidationRevisionSnapshot", "State", "ClaimToken", "AssignedAt",
                    "LeaseExpiresAt", "LastRenewedAt", "LastReconciledAt", "TerminalAt", "Version")
                SELECT
                    claim."RequestId", claim."ProjectId", binding."Id", claim."NodeId",
                    project."RepositoryPath", project."DefaultBranch",
                    binding."ValidationRevision", 'RecoveryRequired', claim."ClaimToken", claim."ClaimedAt",
                    claim."LeaseExpiresAt", NULL, NULL, NULL, claim."Version"
                FROM "RequestClaims" AS claim
                INNER JOIN "WorkRequests" AS request
                    ON request."Id" = claim."RequestId"
                    AND request."ProjectId" = claim."ProjectId"
                INNER JOIN "Projects" AS project
                    ON project."Id" = claim."ProjectId"
                INNER JOIN "WorkspaceBindings" AS binding
                    ON binding."ProjectId" = claim."ProjectId"
                    AND binding."NodeId" = claim."NodeId";
                """);

            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE "__FleetOwnedProjectsCutoverVerification" (
                    "Valid" INTEGER NOT NULL CHECK ("Valid" = 1)
                );

                INSERT INTO "__FleetOwnedProjectsCutoverVerification" ("Valid")
                SELECT CASE WHEN
                    (SELECT COUNT(*) FROM "Projects") =
                        (SELECT COUNT(*) FROM "WorkspaceBindings")
                    AND NOT EXISTS (
                        SELECT 1
                        FROM "Projects" AS project
                        LEFT JOIN "WorkspaceBindings" AS binding
                            ON binding."ProjectId" = project."Id"
                        GROUP BY project."Id"
                        HAVING COUNT(binding."Id") <> 1)
                    AND (SELECT COUNT(*) FROM "RequestClaims") =
                        (SELECT COUNT(*) FROM "ExecutionAssignments")
                    AND NOT EXISTS (
                        SELECT 1
                        FROM "RequestClaims" AS claim
                        LEFT JOIN "WorkRequests" AS request
                            ON request."Id" = claim."RequestId"
                            AND request."ProjectId" = claim."ProjectId"
                        LEFT JOIN "Projects" AS project
                            ON project."Id" = claim."ProjectId"
                        LEFT JOIN "WorkspaceBindings" AS binding
                            ON binding."ProjectId" = claim."ProjectId"
                            AND binding."NodeId" = claim."NodeId"
                        LEFT JOIN "ExecutionAssignments" AS assignment
                            ON assignment."RequestId" = claim."RequestId"
                        WHERE request."Id" IS NULL
                            OR project."Id" IS NULL
                            OR binding."Id" IS NULL
                            OR assignment."RequestId" IS NULL
                            OR assignment."ProjectId" <> claim."ProjectId"
                            OR assignment."WorkspaceBindingId" <> binding."Id"
                            OR assignment."NodeIdSnapshot" <> claim."NodeId"
                            OR assignment."CanonicalRepositoryPathSnapshot" <> project."RepositoryPath"
                            OR assignment."DefaultBranchSnapshot" <> project."DefaultBranch"
                            OR assignment."BindingValidationRevisionSnapshot" <> binding."ValidationRevision"
                            OR assignment."State" <> 'RecoveryRequired'
                            OR assignment."ClaimToken" <> claim."ClaimToken"
                            OR assignment."AssignedAt" <> claim."ClaimedAt"
                            OR assignment."LeaseExpiresAt" <> claim."LeaseExpiresAt"
                            OR assignment."LastRenewedAt" IS NOT NULL
                            OR assignment."LastReconciledAt" IS NOT NULL
                            OR assignment."TerminalAt" IS NOT NULL
                            OR assignment."Version" <> claim."Version")
                    THEN 1 ELSE 0 END;

                DROP TABLE "__FleetOwnedProjectsCutoverVerification";
                """);

            migrationBuilder.DropTable(name: "RequestClaims");

            migrationBuilder.Sql(
                """
                DROP INDEX "IX_Projects_RepositoryPath";
                ALTER TABLE "Projects" DROP COLUMN "NodeId";
                ALTER TABLE "Projects" DROP COLUMN "RepositoryPath";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "The fleet-owned Projects cutover is irreversible because Projects created after it may have no WorkspaceBinding.");
        }
    }
}
