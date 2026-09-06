using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Tests.Completion;

public class VerificationMigrationTests
{
    private const string LegacySchemaMigration = "20260905210000_FleetOwnedProjectsCutover";
    private const string StreamlinedMigration = "20260906172119_StreamlinedVerification";

    private const string FirstRunId = "11111111-1111-4111-8111-111111111111";
    private const string SecondRunId = "22222222-2222-4222-8222-222222222222";
    private const string SharedRequestId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string OmittedKindRunId = "33333333-3333-4333-8333-333333333333";

    [Fact]
    public void Migrate_creates_verification_and_result_tables()
    {
        var path = TestRepositories.CreateSqliteFile();
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var context = new ControlPlaneDbContext(options))
        {
            context.Database.Migrate();
        }

        using var check = new SqliteCommand(
            "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('VerificationRuns','RequestResults') ORDER BY name",
            connection);
        using var reader = check.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        Assert.Equal(["RequestResults", "VerificationRuns"], names);
    }

    [Fact]
    public async Task Streamlined_verification_backfills_legacy_runs_and_adds_policy_schema()
    {
        await using var connection = await CreateLegacySchemaDatabaseAsync();
        await SeedDuplicateLegacyVerificationRunsAsync(connection);

        var recoveryTablesBefore = await ReadRowsAsync(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'Recovery%' ORDER BY name");

        await MigrateAsync(connection, StreamlinedMigration);

        var appliedMigrations = await ReadRowsAsync(
            connection,
            "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId");
        Assert.Contains(LegacySchemaMigration, appliedMigrations);
        Assert.Contains(StreamlinedMigration, appliedMigrations);
        Assert.Equal(StreamlinedMigration, appliedMigrations[^1]);
        Assert.Contains(
            "TrustedVerificationProfileId",
            await ReadColumnNamesAsync(connection, "Projects"));
        Assert.Contains(
            "TrustedVerificationProfileRevision",
            await ReadColumnNamesAsync(connection, "Projects"));

        var assignmentColumns = await ReadColumnNamesAsync(connection, "ExecutionAssignments");
        Assert.Contains("BaselineVersion", assignmentColumns);
        Assert.Contains("MandatoryCommandIdsJson", assignmentColumns);
        Assert.Contains("TrustedVerificationProfileId", assignmentColumns);
        Assert.Contains("TrustedVerificationProfileRevision", assignmentColumns);
        Assert.Contains("VerificationPolicyRevision", assignmentColumns);

        var runColumns = await ReadColumnNamesAsync(connection, "VerificationRuns");
        Assert.Contains("Fingerprint", runColumns);
        Assert.Contains("PolicyRevision", runColumns);
        Assert.Contains("RunKind", runColumns);
        Assert.Contains("AttemptId", runColumns);

        Assert.Equal(
            ["VerificationPolicyUpgradeAudits"],
            await ReadRowsAsync(
                connection,
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'VerificationPolicyUpgradeAudits'"));

        var auditColumns = await ReadColumnNamesAsync(connection, "VerificationPolicyUpgradeAudits");
        Assert.Contains("ProjectId", auditColumns);
        Assert.Contains("ProfileId", auditColumns);
        Assert.Contains("ProfileRevision", auditColumns);
        Assert.Contains("Reason", auditColumns);
        Assert.Contains("MigratedAtUtcTicks", auditColumns);

        var finalIdentityIndex = await ReadRowsAsync(
            connection,
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'IX_VerificationRuns_FinalIdentity'");
        Assert.Single(finalIdentityIndex);
        Assert.Contains("UNIQUE", finalIdentityIndex[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RequestId", finalIdentityIndex[0]);
        Assert.Contains("Fingerprint", finalIdentityIndex[0]);
        Assert.Contains("PolicyRevision", finalIdentityIndex[0]);
        Assert.Contains("ProfileId", finalIdentityIndex[0]);
        Assert.Contains("CommandId", finalIdentityIndex[0]);
        Assert.Contains("RunKind", finalIdentityIndex[0]);
        Assert.Contains("Intermediate", finalIdentityIndex[0]);

        var migratedRuns = await ReadRowsAsync(
            connection,
            """
            SELECT Id, RequestId, ProfileId, CommandId, Fingerprint, PolicyRevision, RunKind, AttemptId
            FROM VerificationRuns
            ORDER BY Id
            """);
        Assert.Equal(
            [
                $"{FirstRunId}|{SharedRequestId}|profile-a|cmd-build|||Intermediate|00000000-0000-0000-0000-000000000000",
                $"{SecondRunId}|{SharedRequestId}|profile-a|cmd-build|||Intermediate|00000000-0000-0000-0000-000000000000",
            ],
            migratedRuns);

        await ExecuteInsertAsync(
            connection,
            expectedRows: 1,
            """
            INSERT INTO VerificationRuns (
                Id, RequestId, ProfileId, CommandId, Status, StartedAtUtcTicks, Mandatory)
            VALUES (
                $id, $requestId, $profileId, $commandId, $status, $startedAt, $mandatory)
            """,
            ("$id", OmittedKindRunId),
            ("$requestId", SharedRequestId),
            ("$profileId", "profile-b"),
            ("$commandId", "cmd-test"),
            ("$status", "Running"),
            ("$startedAt", 5L),
            ("$mandatory", 0));

        Assert.Equal(
            ["Intermediate"],
            await ReadRowsAsync(
                connection,
                $"SELECT RunKind FROM VerificationRuns WHERE Id = '{OmittedKindRunId}'"));

        var recoveryTablesAfter = await ReadRowsAsync(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'Recovery%' ORDER BY name");
        Assert.Equal(recoveryTablesBefore, recoveryTablesAfter);
    }

    private static async Task<SqliteConnection> CreateLegacySchemaDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        try
        {
            await MigrateAsync(connection, LegacySchemaMigration);
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

    private static async Task SeedDuplicateLegacyVerificationRunsAsync(SqliteConnection connection)
    {
        await ExecuteInsertAsync(
            connection,
            expectedRows: 1,
            """
            INSERT INTO VerificationRuns (
                Id, RequestId, ProfileId, CommandId, Status, ExitCode, StartedAtUtcTicks,
                CompletedAtUtcTicks, OutputSummary, OutputArtifactPath, Mandatory)
            VALUES (
                $id, $requestId, $profileId, $commandId, $status, $exitCode, $startedAt,
                $completedAt, $summary, $artifact, $mandatory)
            """,
            ("$id", FirstRunId),
            ("$requestId", SharedRequestId),
            ("$profileId", "profile-a"),
            ("$commandId", "cmd-build"),
            ("$status", "Succeeded"),
            ("$exitCode", 0),
            ("$startedAt", 1L),
            ("$completedAt", 2L),
            ("$summary", "ok"),
            ("$artifact", null),
            ("$mandatory", 1));

        await ExecuteInsertAsync(
            connection,
            expectedRows: 1,
            """
            INSERT INTO VerificationRuns (
                Id, RequestId, ProfileId, CommandId, Status, ExitCode, StartedAtUtcTicks,
                CompletedAtUtcTicks, OutputSummary, OutputArtifactPath, Mandatory)
            VALUES (
                $id, $requestId, $profileId, $commandId, $status, $exitCode, $startedAt,
                $completedAt, $summary, $artifact, $mandatory)
            """,
            ("$id", SecondRunId),
            ("$requestId", SharedRequestId),
            ("$profileId", "profile-a"),
            ("$commandId", "cmd-build"),
            ("$status", "Failed"),
            ("$exitCode", 1),
            ("$startedAt", 3L),
            ("$completedAt", 4L),
            ("$summary", "fail"),
            ("$artifact", null),
            ("$mandatory", 1));
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
                values[index] = reader.IsDBNull(index) ? string.Empty : reader.GetValue(index).ToString() ?? string.Empty;
            }

            rows.Add(string.Join('|', values));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

}
