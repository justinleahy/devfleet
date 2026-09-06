using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Tests.Persistence;

public class FleetOwnershipCutoverMigrationTests
{
    private const string OldSchemaMigration = "20260905201500_AddNodeExecutionStatus";
    private const string CutoverMigration = "20260905210000_FleetOwnedProjectsCutover";

    private const string FirstNodeId = "11111111-1111-1111-1111-111111111111";
    private const string SecondNodeId = "22222222-2222-2222-2222-222222222222";
    private const string FirstProjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string SecondProjectId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string QueuedRequestId = "cccccccc-cccc-cccc-cccc-ccccccccccc1";
    private const string TerminalRequestId = "cccccccc-cccc-cccc-cccc-ccccccccccc2";
    private const string ActiveRequestId = "cccccccc-cccc-cccc-cccc-ccccccccccc3";
    private const long BaselineUtcTicks = 638925408000000000;

    [Fact]
    public async Task Cutover_preserves_queue_and_history_while_moving_legacy_ownership()
    {
        await using var connection = await CreateOldSchemaDatabaseAsync();
        await SeedOldSchemaAsync(connection);

        await MigrateAsync(connection, CutoverMigration);

        Assert.Equal(
            [CutoverMigration, OldSchemaMigration],
            await ReadRowsAsync(
                connection,
                """
                SELECT "MigrationId"
                FROM "__EFMigrationsHistory"
                ORDER BY "MigrationId" DESC
                LIMIT 2;
                """));
        Assert.Equal(
            [FirstNodeId, SecondNodeId],
            await ReadRowsAsync(
                connection,
                """
                SELECT "Id"
                FROM "FleetNodes"
                ORDER BY "Id";
                """));
        Assert.Equal(
            [
                $"{FirstProjectId}|Alpha|main|1|1|4|2|1|1|0|0|{BaselineUtcTicks}|{BaselineUtcTicks}|1",
                $"{SecondProjectId}|Beta|main|1|1|4|2|1|1|0|0|{BaselineUtcTicks}|{BaselineUtcTicks}|1",
            ],
            await ReadRowsAsync(
                connection,
                """
                SELECT
                    "Id", "DisplayName", "DefaultBranch", "Enabled", "MaxActiveWriteRequests",
                    "MaxReadOnlyRequests", "MaxChildAgentsPerRequest", "RequireCleanStart",
                    "CreateRequestBranch", "CreateRequestCommit", "AutoMerge", "CreatedAt",
                    "UpdatedAt", "Version"
                FROM "Projects"
                ORDER BY "Id";
                """));
        Assert.Equal(
            [
                $"{FirstProjectId}|{FirstProjectId}|{FirstNodeId}|/srv/devfleet/alpha|<null>|PendingValidation|1|<null>|<null>|<null>|{BaselineUtcTicks}|{BaselineUtcTicks}|1",
                $"{SecondProjectId}|{SecondProjectId}|{SecondNodeId}|/srv/devfleet/beta|<null>|PendingValidation|1|<null>|<null>|<null>|{BaselineUtcTicks}|{BaselineUtcTicks}|1",
            ],
            await ReadRowsAsync(
                connection,
                """
                SELECT
                    "Id", "ProjectId", "NodeId", "RepositoryPath",
                    COALESCE("CanonicalRepositoryPath", '<null>'), "Status", "ValidationRevision",
                    COALESCE("ValidationCode", '<null>'), COALESCE("ValidationDetail", '<null>'),
                    COALESCE("ValidatedAt", '<null>'), "CreatedAt", "UpdatedAt", "Version"
                FROM "WorkspaceBindings"
                ORDER BY "ProjectId";
                """));
        Assert.Equal(
            [
                $"{TerminalRequestId}|{FirstProjectId}|{FirstProjectId}|{FirstNodeId}|/srv/devfleet/alpha|main|1|RecoveryRequired|terminal-claim|{BaselineUtcTicks + 150}|{BaselineUtcTicks + 250}|<null>|<null>|<null>|3",
                $"{ActiveRequestId}|{SecondProjectId}|{SecondProjectId}|{SecondNodeId}|/srv/devfleet/beta|main|1|RecoveryRequired|active-claim|{BaselineUtcTicks + 350}|{BaselineUtcTicks + 600}|<null>|<null>|<null>|2",
            ],
            await ReadRowsAsync(
                connection,
                """
                SELECT
                    "RequestId", "ProjectId", "WorkspaceBindingId", "NodeIdSnapshot",
                    "CanonicalRepositoryPathSnapshot", "DefaultBranchSnapshot",
                    "BindingValidationRevisionSnapshot", "State", "ClaimToken", "AssignedAt",
                    "LeaseExpiresAt", COALESCE("LastRenewedAt", '<null>'),
                    COALESCE("LastReconciledAt", '<null>'), COALESCE("TerminalAt", '<null>'),
                    "Version"
                FROM "ExecutionAssignments"
                ORDER BY "RequestId";
                """));
        Assert.Equal(
            [
                $"{QueuedRequestId}|{FirstProjectId}|Development|1|Standard|Queued for later|Keep this request queued|Queued|<null>|{BaselineUtcTicks + 100}|{BaselineUtcTicks + 100}|1|<none>",
                $"{TerminalRequestId}|{FirstProjectId}|Development|2|Standard|Completed legacy work|Preserve terminal execution history|Completed|<null>|{BaselineUtcTicks + 200}|{BaselineUtcTicks + 200}|6|RecoveryRequired",
                $"{ActiveRequestId}|{SecondProjectId}|Development|3|Standard|Active legacy work|Recover this in-flight execution|Executing|<null>|{BaselineUtcTicks + 300}|{BaselineUtcTicks + 300}|4|RecoveryRequired",
            ],
            await ReadRowsAsync(
                connection,
                """
                SELECT
                    request."Id", request."ProjectId", request."Kind", request."Priority",
                    request."RiskLevel", request."Title", request."Prompt", request."Status",
                    COALESCE(request."BlockedPhase", '<null>'), request."CreatedAt",
                    request."UpdatedAt", request."Version", COALESCE(assignment."State", '<none>')
                FROM "WorkRequests" AS request
                LEFT JOIN "ExecutionAssignments" AS assignment
                    ON assignment."RequestId" = request."Id"
                ORDER BY request."Id";
                """));
        Assert.Equal(
            [
                $"event-active|{SecondNodeId}|{SecondProjectId}|{ActiveRequestId}|session-active|4|session.log|{BaselineUtcTicks + 500}|{BaselineUtcTicks + 501}|{{\"line\":\"still running\"}}",
                $"event-terminal|{FirstNodeId}|{FirstProjectId}|{TerminalRequestId}|session-terminal|9|request.completed|{BaselineUtcTicks + 400}|{BaselineUtcTicks + 401}|{{\"summaryMarkdown\":\"done\"}}",
            ],
            await ReadRowsAsync(
                connection,
                """
                SELECT
                    "EventId", "NodeId", "ProjectId", "RequestId", "SessionId", "Sequence", "Type",
                    "OccurredAtUtcTicks", "ReceivedAtUtcTicks", "PayloadJson"
                FROM "SessionEvents"
                ORDER BY "EventId";
                """));

        Assert.Empty(
            await ReadRowsAsync(
                connection,
                """
                SELECT "name"
                FROM "sqlite_schema"
                WHERE "type" = 'table' AND "name" = 'RequestClaims';
                """));
        var projectColumns = await ReadRowsAsync(
            connection,
            """SELECT "name" FROM pragma_table_info('Projects') ORDER BY "cid";""");
        Assert.DoesNotContain("NodeId", projectColumns);
        Assert.DoesNotContain("RepositoryPath", projectColumns);
        Assert.Equal(
            [
                "IX_ExecutionAssignments_NodeIdSnapshot_State",
                "IX_ExecutionAssignments_ProjectId_State",
                "IX_ExecutionAssignments_WorkspaceBindingId",
                "IX_WorkspaceBindings_NodeId_CanonicalRepositoryPath",
                "IX_WorkspaceBindings_NodeId_RepositoryPath",
                "IX_WorkspaceBindings_ProjectId",
            ],
            await ReadRowsAsync(
                connection,
                """
                SELECT "name"
                FROM "sqlite_schema"
                WHERE "type" = 'index'
                    AND ("name" LIKE 'IX_ExecutionAssignments_%'
                        OR "name" LIKE 'IX_WorkspaceBindings_%')
                ORDER BY "name";
                """));
    }

    [Fact]
    public async Task Cutover_aborts_atomically_when_a_legacy_claim_has_no_exact_binding()
    {
        await using var connection = await CreateOldSchemaDatabaseAsync();
        await SeedOldSchemaAsync(connection);
        await ExecuteInsertAsync(
            connection,
            expectedRows: 1,
            """
            UPDATE "RequestClaims"
            SET "ProjectId" = $otherProjectId
            WHERE "RequestId" = $requestId;
            """,
            ("$otherProjectId", SecondProjectId),
            ("$requestId", TerminalRequestId));

        await Assert.ThrowsAsync<SqliteException>(() => MigrateAsync(connection, CutoverMigration));

        Assert.Equal(
            [OldSchemaMigration],
            await ReadRowsAsync(
                connection,
                """
                SELECT "MigrationId"
                FROM "__EFMigrationsHistory"
                ORDER BY "MigrationId" DESC
                LIMIT 1;
                """));
        Assert.Equal(
            ["RequestClaims"],
            await ReadRowsAsync(
                connection,
                """
                SELECT "name"
                FROM "sqlite_schema"
                WHERE "type" = 'table' AND "name" = 'RequestClaims';
                """));
        Assert.Empty(
            await ReadRowsAsync(
                connection,
                """
                SELECT "name"
                FROM "sqlite_schema"
                WHERE "type" = 'table'
                    AND "name" IN ('WorkspaceBindings', 'ExecutionAssignments');
                """));
    }

    private static async Task<SqliteConnection> CreateOldSchemaDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        try
        {
            await MigrateAsync(connection, OldSchemaMigration);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task MigrateAsync(SqliteConnection connection, string targetMigration)
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new ControlPlaneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task SeedOldSchemaAsync(SqliteConnection connection)
    {
        await ExecuteInsertAsync(
            connection,
            expectedRows: 2,
            """
            INSERT INTO "FleetNodes" (
                "Id", "DisplayName", "AgentVersion", "Status", "LastHeartbeatAt",
                "CapabilitiesJson", "ResourceSnapshotJson", "CreatedAt", "UpdatedAt", "Version")
            VALUES
                ($firstId, $firstName, $agentVersion, $status, $heartbeatAt,
                 $capabilities, $resourceSnapshot, $createdAt, $updatedAt, $version),
                ($secondId, $secondName, $agentVersion, $status, $heartbeatAt,
                 $capabilities, $resourceSnapshot, $createdAt, $updatedAt, $version);
            """,
            ("$firstId", FirstNodeId),
            ("$firstName", "alpha-node"),
            ("$secondId", SecondNodeId),
            ("$secondName", "beta-node"),
            ("$agentVersion", "1.0.0"),
            ("$status", "Online"),
            ("$heartbeatAt", BaselineUtcTicks),
            ("$capabilities", "{}"),
            ("$resourceSnapshot", null),
            ("$createdAt", BaselineUtcTicks),
            ("$updatedAt", BaselineUtcTicks),
            ("$version", 1L));

        await ExecuteInsertAsync(
            connection,
            expectedRows: 2,
            """
            INSERT INTO "Projects" (
                "Id", "NodeId", "DisplayName", "RepositoryPath", "DefaultBranch", "Enabled",
                "MaxActiveWriteRequests", "MaxReadOnlyRequests", "MaxChildAgentsPerRequest",
                "RequireCleanStart", "CreateRequestBranch", "CreateRequestCommit", "AutoMerge",
                "CreatedAt", "UpdatedAt", "Version")
            VALUES
                ($firstId, $firstNodeId, $firstName, $firstPath, $defaultBranch, $enabled,
                 $maxWrites, $maxReads, $maxChildren, $requireCleanStart, $createBranch,
                 $createCommit, $autoMerge, $createdAt, $updatedAt, $version),
                ($secondId, $secondNodeId, $secondName, $secondPath, $defaultBranch, $enabled,
                 $maxWrites, $maxReads, $maxChildren, $requireCleanStart, $createBranch,
                 $createCommit, $autoMerge, $createdAt, $updatedAt, $version);
            """,
            ("$firstId", FirstProjectId),
            ("$firstNodeId", FirstNodeId),
            ("$firstName", "Alpha"),
            ("$firstPath", "/srv/devfleet/alpha"),
            ("$secondId", SecondProjectId),
            ("$secondNodeId", SecondNodeId),
            ("$secondName", "Beta"),
            ("$secondPath", "/srv/devfleet/beta"),
            ("$defaultBranch", "main"),
            ("$enabled", 1),
            ("$maxWrites", 1),
            ("$maxReads", 4),
            ("$maxChildren", 2),
            ("$requireCleanStart", 1),
            ("$createBranch", 1),
            ("$createCommit", 0),
            ("$autoMerge", 0),
            ("$createdAt", BaselineUtcTicks),
            ("$updatedAt", BaselineUtcTicks),
            ("$version", 1L));

        await ExecuteInsertAsync(
            connection,
            expectedRows: 3,
            """
            INSERT INTO "WorkRequests" (
                "Id", "ProjectId", "Kind", "Priority", "RiskLevel", "Title", "Prompt",
                "Status", "BlockedPhase", "CreatedAt", "UpdatedAt", "Version")
            VALUES
                ($queuedId, $firstProjectId, $kind, $normalPriority, $risk, $queuedTitle,
                 $queuedPrompt, $queuedStatus, $blockedPhase, $queuedAt, $queuedAt, $queuedVersion),
                ($terminalId, $firstProjectId, $kind, $highPriority, $risk, $terminalTitle,
                 $terminalPrompt, $terminalStatus, $blockedPhase, $terminalAt, $terminalAt, $terminalVersion),
                ($activeId, $secondProjectId, $kind, $urgentPriority, $risk, $activeTitle,
                 $activePrompt, $activeStatus, $blockedPhase, $activeAt, $activeAt, $activeVersion);
            """,
            ("$queuedId", QueuedRequestId),
            ("$terminalId", TerminalRequestId),
            ("$activeId", ActiveRequestId),
            ("$firstProjectId", FirstProjectId),
            ("$secondProjectId", SecondProjectId),
            ("$kind", "Development"),
            ("$normalPriority", 1),
            ("$highPriority", 2),
            ("$urgentPriority", 3),
            ("$risk", "Standard"),
            ("$queuedTitle", "Queued for later"),
            ("$queuedPrompt", "Keep this request queued"),
            ("$terminalTitle", "Completed legacy work"),
            ("$terminalPrompt", "Preserve terminal execution history"),
            ("$activeTitle", "Active legacy work"),
            ("$activePrompt", "Recover this in-flight execution"),
            ("$queuedStatus", "Queued"),
            ("$terminalStatus", "Completed"),
            ("$activeStatus", "Executing"),
            ("$blockedPhase", null),
            ("$queuedAt", BaselineUtcTicks + 100),
            ("$terminalAt", BaselineUtcTicks + 200),
            ("$activeAt", BaselineUtcTicks + 300),
            ("$queuedVersion", 1L),
            ("$terminalVersion", 6L),
            ("$activeVersion", 4L));

        await ExecuteInsertAsync(
            connection,
            expectedRows: 2,
            """
            INSERT INTO "SessionEvents" (
                "EventId", "NodeId", "ProjectId", "RequestId", "SessionId", "Sequence", "Type",
                "OccurredAtUtcTicks", "ReceivedAtUtcTicks", "PayloadJson")
            VALUES
                ($terminalEventId, $firstNodeId, $firstProjectId, $terminalRequestId,
                 $terminalSessionId, $terminalSequence, $terminalType, $terminalOccurredAt,
                 $terminalReceivedAt, $terminalPayload),
                ($activeEventId, $secondNodeId, $secondProjectId, $activeRequestId,
                 $activeSessionId, $activeSequence, $activeType, $activeOccurredAt,
                 $activeReceivedAt, $activePayload);
            """,
            ("$terminalEventId", "event-terminal"),
            ("$activeEventId", "event-active"),
            ("$firstNodeId", FirstNodeId),
            ("$secondNodeId", SecondNodeId),
            ("$firstProjectId", FirstProjectId),
            ("$secondProjectId", SecondProjectId),
            ("$terminalRequestId", TerminalRequestId),
            ("$activeRequestId", ActiveRequestId),
            ("$terminalSessionId", "session-terminal"),
            ("$activeSessionId", "session-active"),
            ("$terminalSequence", 9L),
            ("$activeSequence", 4L),
            ("$terminalType", "request.completed"),
            ("$activeType", "session.log"),
            ("$terminalOccurredAt", BaselineUtcTicks + 400),
            ("$terminalReceivedAt", BaselineUtcTicks + 401),
            ("$activeOccurredAt", BaselineUtcTicks + 500),
            ("$activeReceivedAt", BaselineUtcTicks + 501),
            ("$terminalPayload", "{\"summaryMarkdown\":\"done\"}"),
            ("$activePayload", "{\"line\":\"still running\"}"));

        await ExecuteInsertAsync(
            connection,
            expectedRows: 2,
            """
            INSERT INTO "RequestClaims" (
                "RequestId", "ProjectId", "NodeId", "ClaimToken", "ClaimedAt",
                "LeaseExpiresAt", "Version")
            VALUES
                ($terminalRequestId, $firstProjectId, $firstNodeId, $terminalToken,
                 $terminalClaimedAt, $terminalLeaseExpiresAt, $terminalVersion),
                ($activeRequestId, $secondProjectId, $secondNodeId, $activeToken,
                 $activeClaimedAt, $activeLeaseExpiresAt, $activeVersion);
            """,
            ("$terminalRequestId", TerminalRequestId),
            ("$activeRequestId", ActiveRequestId),
            ("$firstProjectId", FirstProjectId),
            ("$secondProjectId", SecondProjectId),
            ("$firstNodeId", FirstNodeId),
            ("$secondNodeId", SecondNodeId),
            ("$terminalToken", "terminal-claim"),
            ("$activeToken", "active-claim"),
            ("$terminalClaimedAt", BaselineUtcTicks + 150),
            ("$terminalLeaseExpiresAt", BaselineUtcTicks + 250),
            ("$activeClaimedAt", BaselineUtcTicks + 350),
            ("$activeLeaseExpiresAt", BaselineUtcTicks + 600),
            ("$terminalVersion", 3L),
            ("$activeVersion", 2L));
    }

    private static async Task ExecuteInsertAsync(
        SqliteConnection connection,
        int expectedRows,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        Assert.Equal(expectedRows, await command.ExecuteNonQueryAsync());
    }

    private static async Task<IReadOnlyList<string>> ReadRowsAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var values = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = reader.GetString(index);
            }

            rows.Add(string.Join('|', values));
        }

        return rows;
    }
}
