using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiCommandCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OriginalRequestId",
                table: "WorkRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkRequests_OriginalRequestId",
                table: "WorkRequests",
                column: "OriginalRequestId");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkRequests_WorkRequests_OriginalRequestId",
                table: "WorkRequests",
                column: "OriginalRequestId",
                principalTable: "WorkRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateTable(
                name: "RecoveryOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    InventoryRevision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BlockerCodesJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    EvidenceJson = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    DeadlineUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    LastProgressUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecoveryAuditFacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    AtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryAuditFacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecoveryAuditFacts_RecoveryOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "RecoveryOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecoveryHolds",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EstablishedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryHolds", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_RecoveryHolds_RecoveryOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "RecoveryOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecoveryIdempotencyKeys",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    InputHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryIdempotencyKeys", x => new { x.ProjectId, x.Action, x.Key });
                    table.ForeignKey(
                        name: "FK_RecoveryIdempotencyKeys_RecoveryOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "RecoveryOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecoveryReservationTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LeaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CapturedVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CapturedState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    EvidenceJson = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryReservationTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecoveryReservationTargets_RecoveryOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "RecoveryOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecoveryTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CapturedVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CapturedState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    BindingRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    EvidenceJson = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecoveryTargets_RecoveryOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "RecoveryOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PendingTerminalizations",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClaimToken = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RootSessionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Intent = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CompletionEvidenceJson = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    AcceptedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingTerminalizations", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_PendingTerminalizations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PendingTerminalizations_WorkRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "WorkRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });


            migrationBuilder.CreateIndex(
                name: "IX_RecoveryAuditFacts_OperationId_At",
                table: "RecoveryAuditFacts",
                columns: new[] { "OperationId", "AtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryHolds_OperationId",
                table: "RecoveryHolds",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryIdempotencyKeys_OperationId",
                table: "RecoveryIdempotencyKeys",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryOperations_ProjectId_CreatedAt",
                table: "RecoveryOperations",
                columns: new[] { "ProjectId", "CreatedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryOperations_ProjectId_Unresolved",
                table: "RecoveryOperations",
                column: "ProjectId",
                unique: true,
                filter: "\"Status\" <> 'Recovered'");

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryReservationTargets_OperationId_LeaseId",
                table: "RecoveryReservationTargets",
                columns: new[] { "OperationId", "LeaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryTargets_OperationId_RequestId",
                table: "RecoveryTargets",
                columns: new[] { "OperationId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingTerminalizations_NodeId",
                table: "PendingTerminalizations",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTerminalizations_ProjectId",
                table: "PendingTerminalizations",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkRequests_WorkRequests_OriginalRequestId",
                table: "WorkRequests");

            migrationBuilder.DropIndex(
                name: "IX_WorkRequests_OriginalRequestId",
                table: "WorkRequests");

            migrationBuilder.DropColumn(
                name: "OriginalRequestId",
                table: "WorkRequests");

            migrationBuilder.DropTable(
                name: "PendingTerminalizations");

            migrationBuilder.DropTable(
                name: "RecoveryAuditFacts");

            migrationBuilder.DropTable(
                name: "RecoveryHolds");

            migrationBuilder.DropTable(
                name: "RecoveryIdempotencyKeys");

            migrationBuilder.DropTable(
                name: "RecoveryReservationTargets");

            migrationBuilder.DropTable(
                name: "RecoveryTargets");

            migrationBuilder.DropTable(
                name: "RecoveryOperations");
        }
    }
}
