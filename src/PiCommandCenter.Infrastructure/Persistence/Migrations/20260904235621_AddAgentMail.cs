using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiCommandCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentMail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailAgentIdentities",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Runtime = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AllocatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailAgentIdentities", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "MailMessages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ThreadId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SenderSessionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BodyMarkdown = table.Column<string>(type: "TEXT", nullable: false),
                    Importance = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AcknowledgementRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectFencingTokens",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastFencingToken = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectFencingTokens", x => x.ProjectId);
                });

            migrationBuilder.CreateTable(
                name: "ReservationAuditFacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LeaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    RepositoryStatusSnapshot = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservationAuditFacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReservationLeases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerSessionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    FencingToken = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AcquiredAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRenewedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ReleasedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservationLeases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MailRecipients",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ReadAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    AcknowledgedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailRecipients", x => new { x.MessageId, x.SessionId });
                    table.ForeignKey(
                        name: "FK_MailRecipients_MailMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "MailMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReservationScopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LeaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservationScopes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReservationScopes_ReservationLeases_LeaseId",
                        column: x => x.LeaseId,
                        principalTable: "ReservationLeases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailAgentIdentities_ProjectName",
                table: "MailAgentIdentities",
                columns: new[] { "ProjectId", "AgentName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailMessages_Thread",
                table: "MailMessages",
                columns: new[] { "ProjectId", "ThreadId", "CreatedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_MailRecipients_Inbox",
                table: "MailRecipients",
                columns: new[] { "SessionId", "ReadAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_ReservationAuditFacts_LeaseId_At",
                table: "ReservationAuditFacts",
                columns: new[] { "LeaseId", "AtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_ReservationLeases_OwnerSessionId",
                table: "ReservationLeases",
                column: "OwnerSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ReservationLeases_ProjectId_State_ExpiresAt",
                table: "ReservationLeases",
                columns: new[] { "ProjectId", "State", "ExpiresAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_ReservationScopes_LeaseId",
                table: "ReservationScopes",
                column: "LeaseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailAgentIdentities");

            migrationBuilder.DropTable(
                name: "MailRecipients");

            migrationBuilder.DropTable(
                name: "ProjectFencingTokens");

            migrationBuilder.DropTable(
                name: "ReservationAuditFacts");

            migrationBuilder.DropTable(
                name: "ReservationScopes");

            migrationBuilder.DropTable(
                name: "MailMessages");

            migrationBuilder.DropTable(
                name: "ReservationLeases");
        }
    }
}
