using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Runtime;

namespace PiCommandCenter.Node;

/// <summary>SQLite-backed node assignment journal stored beside the pending event spool.</summary>
public sealed class SqliteNodeAssignmentJournal : INodeAssignmentJournal
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
    };

    private const int MaxProcessIdentities = 16;
    private const int MaxScopeNameLength = 128;
    private const int MaxProcessIdentitiesJsonLength = 8192;


    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private int _disposed;

    public SqliteNodeAssignmentJournal(IOptions<NodeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = options.Value.EventSpoolPath;
    }

    public async Task<IReadOnlyList<NodeAssignmentJournalEntry>> LoadAsync(
        CancellationToken cancellationToken)
    {
        return await WithConnectionAsync(async connection =>
        {
            var entries = new List<NodeAssignmentJournalEntry>();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT RequestId, AssignmentJson, SupervisorState, RepositoryKnown, PendingEventCount, ProcessIdentitiesJson
                FROM NodeAssignments
                ORDER BY RequestId;
                """;


            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(ReadEntry(reader));
            }

            return (IReadOnlyList<NodeAssignmentJournalEntry>)entries;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertAsync(
        NodeAssignmentJournalEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Assignment);
        ArgumentOutOfRangeException.ThrowIfNegative(entry.PendingEventCount);
        var processIdentitiesJson = SerializeProcessIdentities(entry.ProcessIdentities);

        var assignmentJson = JsonSerializer.Serialize(entry.Assignment, SerializerOptions);
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO NodeAssignments (
                    RequestId,
                    AssignmentJson,
                    SupervisorState,
                    RepositoryKnown,
                    PendingEventCount,
                    ProcessIdentitiesJson)
                VALUES (
                    $requestId,
                    $assignmentJson,
                    $supervisorState,
                    $repositoryKnown,
                    $pendingEventCount,
                    $processIdentitiesJson)
                ON CONFLICT(RequestId) DO UPDATE SET
                    AssignmentJson = excluded.AssignmentJson,
                    SupervisorState = excluded.SupervisorState,
                    RepositoryKnown = excluded.RepositoryKnown,
                    PendingEventCount = excluded.PendingEventCount,
                    ProcessIdentitiesJson = excluded.ProcessIdentitiesJson;
                """;
            command.Parameters.AddWithValue("$requestId", entry.Assignment.RequestId.ToString("D"));
            command.Parameters.AddWithValue("$assignmentJson", assignmentJson);
            command.Parameters.AddWithValue("$supervisorState", entry.SupervisorState.ToString());
            command.Parameters.AddWithValue("$repositoryKnown", entry.RepositoryKnown ? 1 : 0);
            command.Parameters.AddWithValue("$pendingEventCount", entry.PendingEventCount);
            command.Parameters.AddWithValue(
                "$processIdentitiesJson",
                processIdentitiesJson is null ? DBNull.Value : processIdentitiesJson);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM NodeAssignments WHERE RequestId = $requestId;";
            command.Parameters.AddWithValue("$requestId", requestId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static NodeAssignmentJournalEntry ReadEntry(SqliteDataReader reader)
    {
        var persistedRequestId = ReadRequestId(reader);
        try
        {
            var assignmentJson = reader.GetString(1);
            var assignment = JsonSerializer.Deserialize<ExecutionAssignmentMessage>(
                assignmentJson,
                SerializerOptions)
                ?? throw Corrupt(persistedRequestId, "The assignment payload is null.");

            if (assignment.RequestId != persistedRequestId)
            {
                throw Corrupt(
                    persistedRequestId,
                    $"The assignment payload identifies request '{assignment.RequestId:D}'.");
            }

            EnsureCompleteAssignment(persistedRequestId, assignment);
            var persistedSupervisorState = ReadSupervisorState(reader, persistedRequestId);
            var repositoryKnown = ReadBoolean(reader, 3, "RepositoryKnown", persistedRequestId);
            var pendingEventCount = reader.GetInt32(4);
            if (pendingEventCount < 0)
            {
                throw Corrupt(persistedRequestId, "PendingEventCount cannot be negative.");
            }

            var processIdentities = ReadProcessIdentities(reader, persistedRequestId);

            return new NodeAssignmentJournalEntry(
                assignment,
                persistedSupervisorState == AssignmentSupervisorState.StartBlocked
                    ? AssignmentSupervisorState.StartBlocked
                    : AssignmentSupervisorState.Unknown,
                repositoryKnown,
                pendingEventCount,
                processIdentities);

        }
        catch (Exception exception) when (exception is JsonException
            or InvalidCastException
            or InvalidOperationException
            or FormatException
            or OverflowException)
        {
            throw Corrupt(persistedRequestId, "The persisted assignment row is malformed.", exception);
        }
    }

    private static Guid ReadRequestId(SqliteDataReader reader)
    {
        try
        {
            var value = reader.GetString(0);
            if (Guid.TryParseExact(value, "D", out var requestId))
            {
                return requestId;
            }

            throw Corrupt(null, $"The persisted request id '{value}' is malformed.");
        }
        catch (Exception exception) when (exception is InvalidCastException
            or InvalidOperationException
            or FormatException)
        {
            throw Corrupt(null, "The persisted request id is malformed.", exception);
        }
    }

    private static AssignmentSupervisorState ReadSupervisorState(
        SqliteDataReader reader,
        Guid requestId)
    {
        var value = reader.GetString(2);
        if (Enum.TryParse<AssignmentSupervisorState>(value, ignoreCase: false, out var state)
            && Enum.IsDefined(state))
        {
            return state;
        }

        throw Corrupt(requestId, $"SupervisorState '{value}' is not recognized.");
    }

    private static bool ReadBoolean(
        SqliteDataReader reader,
        int ordinal,
        string name,
        Guid requestId)
    {
        return reader.GetInt64(ordinal) switch
        {
            0 => false,
            1 => true,
            var value => throw Corrupt(requestId, $"{name} value '{value}' is malformed."),
        };
    }

    private static string? SerializeProcessIdentities(
        IReadOnlyList<AssignmentProcessIdentity>? identities)
    {
        if (identities is null || identities.Count == 0)
        {
            return null;
        }

        ValidateProcessIdentities(identities, requestId: null);
        var json = JsonSerializer.Serialize(identities, SerializerOptions);
        if (json.Length > MaxProcessIdentitiesJsonLength)
        {
            throw new ArgumentException(
                "ProcessIdentities JSON exceeds the persistence bound.",
                nameof(identities));
        }

        return json;
    }

    private static IReadOnlyList<AssignmentProcessIdentity> ReadProcessIdentities(
        SqliteDataReader reader,
        Guid requestId)
    {
        if (reader.FieldCount < 6 || reader.IsDBNull(5))
        {
            return [];
        }

        var json = reader.GetString(5);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        if (json.Length > MaxProcessIdentitiesJsonLength)
        {
            throw Corrupt(requestId, "ProcessIdentitiesJson exceeds the persistence bound.");
        }

        List<AssignmentProcessIdentity>? identities;
        try
        {
            identities = JsonSerializer.Deserialize<List<AssignmentProcessIdentity>>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw Corrupt(requestId, "ProcessIdentitiesJson is malformed.", exception);
        }

        if (identities is null)
        {
            throw Corrupt(requestId, "ProcessIdentitiesJson is null.");
        }

        ValidateProcessIdentities(identities, requestId);
        return identities;
    }

    private static void ValidateProcessIdentities(
        IReadOnlyList<AssignmentProcessIdentity> identities,
        Guid? requestId)
    {
        if (identities.Count > MaxProcessIdentities)
        {
            ThrowIdentityCorrupt(requestId, "ProcessIdentities exceeds the persistence bound.");
        }

        foreach (var identity in identities)
        {
            if (identity.ProcessId <= 0
                || identity.StartTimeTicks <= 0
                || identity.ProcessGroupId <= 0
                || identity.SessionId <= 0)
            {
                ThrowIdentityCorrupt(
                    requestId,
                    "Process identity fields must be positive.");
            }

            if (identity.ScopeName is { Length: > MaxScopeNameLength })
            {
                ThrowIdentityCorrupt(
                    requestId,
                    "Process identity scope name exceeds the persistence bound.");
            }
        }
    }

    private static void ThrowIdentityCorrupt(Guid? requestId, string detail)
    {
        if (requestId is { } id)
        {
            throw Corrupt(id, detail);
        }

        throw new ArgumentException(detail);
    }


    private static void EnsureCompleteAssignment(
        Guid requestId,
        ExecutionAssignmentMessage assignment)
    {
        if (assignment.RequestId == Guid.Empty
            || assignment.ProjectId == Guid.Empty
            || assignment.WorkspaceBindingId == Guid.Empty
            || assignment.NodeIdSnapshot == Guid.Empty
            || assignment.CanonicalRepositoryPathSnapshot is null
            || assignment.DefaultBranchSnapshot is null
            || assignment.State is null
            || assignment.ClaimToken is null
            || assignment.RequestTitle is null
            || assignment.RequestPrompt is null
            || assignment.RequestKind is null
            || assignment.RequestRiskLevel is null)
        {
            throw Corrupt(requestId, "The assignment payload is incomplete.");
        }
    }

    private static NodeAssignmentJournalCorruptionException Corrupt(
        Guid? requestId,
        string detail,
        Exception? innerException = null)
    {
        var identity = requestId is { } id ? $" for request '{id:D}'" : string.Empty;
        return new NodeAssignmentJournalCorruptionException(
            $"The node assignment journal row{identity} cannot be loaded. {detail}",
            innerException);
    }

    private async Task<SqliteConnection> EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_connection is { } open)
        {
            return open;
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS NodeAssignments (
                    RequestId TEXT PRIMARY KEY,
                    AssignmentJson TEXT NOT NULL,
                    SupervisorState TEXT NOT NULL,
                    RepositoryKnown INTEGER NOT NULL,
                    PendingEventCount INTEGER NOT NULL,
                    ProcessIdentitiesJson TEXT
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureProcessIdentitiesColumnAsync(connection, cancellationToken).ConfigureAwait(false);


        _connection = connection;
        return connection;
    }

    private static async Task EnsureProcessIdentitiesColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(NodeAssignments);";
        var hasColumn = false;
        await using (var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "ProcessIdentitiesJson", StringComparison.Ordinal))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
        {
            return;
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE NodeAssignments ADD COLUMN ProcessIdentitiesJson TEXT;";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }


    private async Task WithConnectionAsync(
        Func<SqliteConnection, Task> action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            await action(connection).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> WithConnectionAsync<T>(
        Func<SqliteConnection, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            return await action(connection).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
