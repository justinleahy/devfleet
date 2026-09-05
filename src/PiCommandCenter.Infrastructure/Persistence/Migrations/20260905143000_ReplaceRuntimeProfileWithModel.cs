using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiCommandCenter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceRuntimeProfileWithModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite cannot widen a NOT NULL TEXT column in place. Rebuild so existing
            // session rows survive, then project legacy RuntimeProfile values onto the
            // canonical selector before the new shape is enforced.
            migrationBuilder.Sql(
                """
                CREATE TABLE "AgentSessions_tmp" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AgentSessions" PRIMARY KEY,
                    "Activity" TEXT NOT NULL,
                    "AgentName" TEXT NOT NULL,
                    "Attention" TEXT NOT NULL,
                    "CurrentOperation" TEXT NULL,
                    "EndedAtUtcTicks" INTEGER NULL,
                    "LastHeartbeatAtUtcTicks" INTEGER NULL,
                    "LastSequence" INTEGER NOT NULL,
                    "Liveness" TEXT NOT NULL,
                    "Model" TEXT NOT NULL,
                    "ParentSessionId" TEXT NULL,
                    "ProcessId" INTEGER NULL,
                    "ProjectId" TEXT NOT NULL,
                    "ProviderSessionId" TEXT NULL,
                    "RequestId" TEXT NOT NULL,
                    "Role" TEXT NOT NULL,
                    "Runtime" TEXT NOT NULL,
                    "StartedAtUtcTicks" INTEGER NOT NULL,
                    "StatusReason" TEXT NOT NULL,
                    "Version" INTEGER NOT NULL,
                    "WorkState" TEXT NOT NULL
                );

                INSERT INTO "AgentSessions_tmp" (
                    "Id", "Activity", "AgentName", "Attention", "CurrentOperation",
                    "EndedAtUtcTicks", "LastHeartbeatAtUtcTicks", "LastSequence", "Liveness",
                    "Model", "ParentSessionId", "ProcessId", "ProjectId", "ProviderSessionId",
                    "RequestId", "Role", "Runtime", "StartedAtUtcTicks", "StatusReason",
                    "Version", "WorkState")
                SELECT
                    "Id", "Activity", "AgentName", "Attention", "CurrentOperation",
                    "EndedAtUtcTicks", "LastHeartbeatAtUtcTicks", "LastSequence", "Liveness",
                    CASE "Runtime"
                        WHEN 'pi' THEN 'codex/default'
                        WHEN 'claude-code' THEN 'claude-code/default'
                        WHEN 'antigravity' THEN 'antigravity/default'
                        ELSE 'codex/default'
                    END,
                    "ParentSessionId", "ProcessId", "ProjectId", "ProviderSessionId",
                    "RequestId", "Role", "Runtime", "StartedAtUtcTicks", "StatusReason",
                    "Version", "WorkState"
                FROM "AgentSessions";

                DROP TABLE "AgentSessions";
                ALTER TABLE "AgentSessions_tmp" RENAME TO "AgentSessions";

                CREATE INDEX "IX_AgentSessions_ParentSessionId" ON "AgentSessions" ("ParentSessionId");
                CREATE INDEX "IX_AgentSessions_RequestId" ON "AgentSessions" ("RequestId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "AgentSessions_tmp" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AgentSessions" PRIMARY KEY,
                    "Activity" TEXT NOT NULL,
                    "AgentName" TEXT NOT NULL,
                    "Attention" TEXT NOT NULL,
                    "CurrentOperation" TEXT NULL,
                    "EndedAtUtcTicks" INTEGER NULL,
                    "LastHeartbeatAtUtcTicks" INTEGER NULL,
                    "LastSequence" INTEGER NOT NULL,
                    "Liveness" TEXT NOT NULL,
                    "ParentSessionId" TEXT NULL,
                    "ProcessId" INTEGER NULL,
                    "ProjectId" TEXT NOT NULL,
                    "ProviderSessionId" TEXT NULL,
                    "RequestId" TEXT NOT NULL,
                    "Role" TEXT NOT NULL,
                    "Runtime" TEXT NOT NULL,
                    "RuntimeProfile" TEXT NOT NULL,
                    "StartedAtUtcTicks" INTEGER NOT NULL,
                    "StatusReason" TEXT NOT NULL,
                    "Version" INTEGER NOT NULL,
                    "WorkState" TEXT NOT NULL
                );

                INSERT INTO "AgentSessions_tmp" (
                    "Id", "Activity", "AgentName", "Attention", "CurrentOperation",
                    "EndedAtUtcTicks", "LastHeartbeatAtUtcTicks", "LastSequence", "Liveness",
                    "ParentSessionId", "ProcessId", "ProjectId", "ProviderSessionId",
                    "RequestId", "Role", "Runtime", "RuntimeProfile", "StartedAtUtcTicks",
                    "StatusReason", "Version", "WorkState")
                SELECT
                    "Id", "Activity", "AgentName", "Attention", "CurrentOperation",
                    "EndedAtUtcTicks", "LastHeartbeatAtUtcTicks", "LastSequence", "Liveness",
                    "ParentSessionId", "ProcessId", "ProjectId", "ProviderSessionId",
                    "RequestId", "Role", "Runtime",
                    CASE
                        WHEN instr("Model", '/') > 0 THEN substr("Model", instr("Model", '/') + 1, 64)
                        ELSE substr("Model", 1, 64)
                    END,
                    "StartedAtUtcTicks", "StatusReason", "Version", "WorkState"
                FROM "AgentSessions";

                DROP TABLE "AgentSessions";
                ALTER TABLE "AgentSessions_tmp" RENAME TO "AgentSessions";

                CREATE INDEX "IX_AgentSessions_ParentSessionId" ON "AgentSessions" ("ParentSessionId");
                CREATE INDEX "IX_AgentSessions_RequestId" ON "AgentSessions" ("RequestId");
                """);
        }
    }
}
