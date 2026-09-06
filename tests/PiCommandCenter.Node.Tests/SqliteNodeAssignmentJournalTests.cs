using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node;
using PiCommandCenter.Node.Runtime;

namespace PiCommandCenter.Node.Tests;

public sealed class SqliteNodeAssignmentJournalTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public SqliteNodeAssignmentJournalTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "pi-cc-node-journal-tests",
            Guid.NewGuid().ToString("N"));
        _databasePath = Path.Combine(_directory, "node-spool.db");
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Assignment_snapshot_and_token_survive_restart_with_unknown_supervisor_state()
    {
        var assignment = MakeAssignment(Guid.NewGuid(), marker: "original");
        await using (var journal = CreateJournal())
        {
            await journal.UpsertAsync(
                new NodeAssignmentJournalEntry(
                    assignment,
                    AssignmentSupervisorState.Running,
                    RepositoryKnown: true,
                    PendingEventCount: 7),
                CancellationToken.None);
        }

        await using var restarted = CreateJournal();
        var loaded = Assert.Single(await restarted.LoadAsync(CancellationToken.None));

        Assert.Equal(assignment, loaded.Assignment);
        Assert.Equal(assignment.ClaimToken, loaded.Assignment.ClaimToken);
        Assert.Equal(AssignmentSupervisorState.Unknown, loaded.SupervisorState);
        Assert.True(loaded.RepositoryKnown);
        Assert.Equal(7, loaded.PendingEventCount);
        Assert.Empty(loaded.ProcessIdentities ?? []);

    }
    [Fact]
    public async Task Start_blocked_supervisor_state_survives_restart_for_safe_retry()
    {
        var assignment = MakeAssignment(Guid.NewGuid(), marker: "start-blocked");
        await using (var journal = CreateJournal())
        {
            await journal.UpsertAsync(
                new NodeAssignmentJournalEntry(
                    assignment,
                    AssignmentSupervisorState.StartBlocked,
                    RepositoryKnown: false,
                    PendingEventCount: 1),
                CancellationToken.None);
        }

        await using var restarted = CreateJournal();
        var loaded = Assert.Single(await restarted.LoadAsync(CancellationToken.None));

        Assert.Equal(AssignmentSupervisorState.StartBlocked, loaded.SupervisorState);
        Assert.False(loaded.RepositoryKnown);
        Assert.Equal(1, loaded.PendingEventCount);
    }


    [Fact]
    public async Task Upsert_replaces_the_complete_entry_and_delete_removes_only_the_requested_assignment()
    {
        var replacedRequestId = Guid.NewGuid();
        var retainedRequestId = Guid.NewGuid();
        var replacement = new NodeAssignmentJournalEntry(
            MakeAssignment(replacedRequestId, marker: "replacement"),
            AssignmentSupervisorState.Stopped,
            RepositoryKnown: false,
            PendingEventCount: 11);
        var retained = new NodeAssignmentJournalEntry(
            MakeAssignment(retainedRequestId, marker: "retained"),
            AssignmentSupervisorState.Running,
            RepositoryKnown: true,
            PendingEventCount: 3);

        await using var journal = CreateJournal();
        await journal.UpsertAsync(
            new NodeAssignmentJournalEntry(
                MakeAssignment(replacedRequestId, marker: "original"),
                AssignmentSupervisorState.Running,
                RepositoryKnown: true,
                PendingEventCount: 1),
            CancellationToken.None);
        await journal.UpsertAsync(retained, CancellationToken.None);
        await journal.UpsertAsync(replacement, CancellationToken.None);

        var afterReplacement = await journal.LoadAsync(CancellationToken.None);
        Assert.Equal(2, afterReplacement.Count);
        var loadedReplacement = Assert.Single(
            afterReplacement, entry => entry.Assignment.RequestId == replacedRequestId);
        Assert.Equal(replacement.Assignment, loadedReplacement.Assignment);
        Assert.Equal(AssignmentSupervisorState.Unknown, loadedReplacement.SupervisorState);
        Assert.Equal(replacement.RepositoryKnown, loadedReplacement.RepositoryKnown);
        Assert.Equal(replacement.PendingEventCount, loadedReplacement.PendingEventCount);

        await journal.DeleteAsync(replacedRequestId, CancellationToken.None);
        await journal.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

        var remaining = Assert.Single(await journal.LoadAsync(CancellationToken.None));
        Assert.Equal(retained.Assignment, remaining.Assignment);
        Assert.Equal(retained.RepositoryKnown, remaining.RepositoryKnown);
        Assert.Equal(retained.PendingEventCount, remaining.PendingEventCount);
    }

    [Fact]
    public async Task Load_fails_closed_when_a_persisted_row_is_malformed()
    {
        var requestId = Guid.NewGuid();
        await using (var journal = CreateJournal())
        {
            await journal.UpsertAsync(
                new NodeAssignmentJournalEntry(
                    MakeAssignment(requestId, marker: "corrupt-me"),
                    AssignmentSupervisorState.Running,
                    RepositoryKnown: true,
                    PendingEventCount: 0),
                CancellationToken.None);
        }

        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE NodeAssignments SET AssignmentJson = '{not-json' WHERE RequestId = $requestId;";
            command.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await using var restarted = CreateJournal();
        var exception = await Assert.ThrowsAsync<NodeAssignmentJournalCorruptionException>(
            () => restarted.LoadAsync(CancellationToken.None));
        Assert.Contains(requestId.ToString("D"), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Process_identities_round_trip_across_restart()
    {
        var assignment = MakeAssignment(Guid.NewGuid(), marker: "identities");
        var identities = new[]
        {
            new AssignmentProcessIdentity(4242, 1_000_001, 4242, 7, "pi-worker"),
            new AssignmentProcessIdentity(4243, 1_000_002, 4242, 7, null),
        };

        await using (var journal = CreateJournal())
        {
            await journal.UpsertAsync(
                new NodeAssignmentJournalEntry(
                    assignment,
                    AssignmentSupervisorState.Running,
                    RepositoryKnown: true,
                    PendingEventCount: 2,
                    identities),
                CancellationToken.None);
        }

        await using var restarted = CreateJournal();
        var loaded = Assert.Single(await restarted.LoadAsync(CancellationToken.None));
        Assert.Equal(AssignmentSupervisorState.Unknown, loaded.SupervisorState);
        Assert.Equal(identities, loaded.ProcessIdentities);
    }

    [Fact]
    public async Task Legacy_schema_migrates_and_loads_missing_identities_as_empty()
    {
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE NodeAssignments (
                    RequestId TEXT PRIMARY KEY,
                    AssignmentJson TEXT NOT NULL,
                    SupervisorState TEXT NOT NULL,
                    RepositoryKnown INTEGER NOT NULL,
                    PendingEventCount INTEGER NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();
        }

        var assignment = MakeAssignment(Guid.NewGuid(), marker: "legacy");
        await using (var seed = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await seed.OpenAsync();
            await using var insert = seed.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO NodeAssignments (
                    RequestId, AssignmentJson, SupervisorState, RepositoryKnown, PendingEventCount)
                VALUES ($requestId, $assignmentJson, $supervisorState, 1, 0);
                """;
            insert.Parameters.AddWithValue("$requestId", assignment.RequestId.ToString("D"));
            insert.Parameters.AddWithValue(
                "$assignmentJson",
                System.Text.Json.JsonSerializer.Serialize(
                    assignment,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                    {
                        RespectRequiredConstructorParameters = true,
                    }));
            insert.Parameters.AddWithValue("$supervisorState", nameof(AssignmentSupervisorState.Running));
            await insert.ExecuteNonQueryAsync();
        }

        await using var journal = CreateJournal();
        var loaded = Assert.Single(await journal.LoadAsync(CancellationToken.None));
        Assert.Equal(assignment.RequestId, loaded.Assignment.RequestId);
        Assert.Equal(AssignmentSupervisorState.Unknown, loaded.SupervisorState);
        Assert.Empty(loaded.ProcessIdentities ?? []);
    }

    [Fact]
    public async Task Load_fails_closed_when_process_identities_payload_is_corrupt()
    {
        var requestId = Guid.NewGuid();
        await using (var journal = CreateJournal())
        {
            await journal.UpsertAsync(
                new NodeAssignmentJournalEntry(
                    MakeAssignment(requestId, marker: "identity-corrupt"),
                    AssignmentSupervisorState.Running,
                    RepositoryKnown: true,
                    PendingEventCount: 0),
                CancellationToken.None);
        }

        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE NodeAssignments SET ProcessIdentitiesJson = '{not-json' WHERE RequestId = $requestId;";
            command.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await using var restarted = CreateJournal();
        var exception = await Assert.ThrowsAsync<NodeAssignmentJournalCorruptionException>(
            () => restarted.LoadAsync(CancellationToken.None));
        Assert.Contains(requestId.ToString("D"), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_fails_closed_when_process_identity_fields_are_not_positive()
    {
        var requestId = Guid.NewGuid();
        await using (var journal = CreateJournal())
        {
            await journal.UpsertAsync(
                new NodeAssignmentJournalEntry(
                    MakeAssignment(requestId, marker: "identity-invalid"),
                    AssignmentSupervisorState.Running,
                    RepositoryKnown: true,
                    PendingEventCount: 0),
                CancellationToken.None);
        }

        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE NodeAssignments
                SET ProcessIdentitiesJson = '[{"processId":0,"startTimeTicks":1,"processGroupId":1,"sessionId":1}]'
                WHERE RequestId = $requestId;
                """;
            command.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await using var restarted = CreateJournal();
        await Assert.ThrowsAsync<NodeAssignmentJournalCorruptionException>(
            () => restarted.LoadAsync(CancellationToken.None));
    }


    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SqliteNodeAssignmentJournal CreateJournal()
        => new(Options.Create(new NodeOptions { EventSpoolPath = _databasePath }));

    private static ExecutionAssignmentMessage MakeAssignment(Guid requestId, string marker)
    {
        var assignedAt = new DateTimeOffset(2026, 9, 5, 8, 15, 30, TimeSpan.FromHours(-4));
        return new ExecutionAssignmentMessage(
            requestId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"/srv/repos/{marker}",
            $"branch-{marker}",
            BindingValidationRevisionSnapshot: marker.Length * 13L,
            State: $"state-{marker}",
            ClaimToken: $"token-{marker}",
            AssignedAt: assignedAt.AddMinutes(marker.Length),
            LeaseExpiresAt: assignedAt.AddHours(marker.Length),
            RequestTitle: $"title-{marker}",
            RequestPrompt: $"prompt-{marker}",
            RequestKind: $"kind-{marker}",
            RequestRiskLevel: $"risk-{marker}",
            CreateRequestBranch: marker.Length % 2 == 0,
            CreateRequestCommit: marker.Length % 3 == 0);
    }
}
