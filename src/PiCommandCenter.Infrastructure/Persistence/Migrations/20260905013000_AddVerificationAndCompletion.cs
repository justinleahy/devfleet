using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiCommandCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerificationAndCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VerificationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CommandId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    OutputSummary = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    OutputArtifactPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Mandatory = table.Column<bool>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VerificationRuns_RequestId",
                table: "VerificationRuns",
                column: "RequestId");

            migrationBuilder.CreateTable(
                name: "RequestResults",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SummaryMarkdown = table.Column<string>(type: "TEXT", nullable: false),
                    ChangedFilesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReviewFindingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    VerificationSummaryJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestResults", x => x.RequestId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "VerificationRuns");
            migrationBuilder.DropTable(name: "RequestResults");
        }
    }
}
