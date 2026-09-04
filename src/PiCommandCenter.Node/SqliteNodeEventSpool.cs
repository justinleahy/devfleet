using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node;

/// <summary>
/// SQLite-backed <see cref="INodeEventSpool"/>. The spool database is a private
/// local file; the whole <see cref="NodeEventMessage"/> is serialized so replayed
/// events are byte-identical to what was originally published.
/// </summary>
public sealed class SqliteNodeEventSpool : INodeEventSpool
{
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;

    public SqliteNodeEventSpool(IOptions<NodeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = options.Value.EventSpoolPath;
    }

    public async Task AppendAsync(NodeEventMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payloadJson = JsonSerializer.Serialize(message, PayloadSerializerOptions);
        var occurredAtUtcTicks = message.OccurredAt.ToUniversalTime().Ticks;
        var insertedAtUtcTicks = DateTime.UtcNow.Ticks;

        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO PendingEvents (EventId, PayloadJson, OccurredAtUtcTicks, InsertedAtUtcTicks)
                VALUES ($eventId, $payloadJson, $occurredAtUtcTicks, $insertedAtUtcTicks)
                ON CONFLICT(EventId) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$eventId", message.EventId);
            command.Parameters.AddWithValue("$payloadJson", payloadJson);
            command.Parameters.AddWithValue("$occurredAtUtcTicks", occurredAtUtcTicks);
            command.Parameters.AddWithValue("$insertedAtUtcTicks", insertedAtUtcTicks);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NodeEventMessage>> PeekPendingAsync(int max, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        var messages = await WithConnectionAsync(async connection =>
        {
            var results = new List<NodeEventMessage>();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EventId, PayloadJson
                FROM PendingEvents
                ORDER BY InsertedAtUtcTicks, rowid
                LIMIT $max;
                """;
            command.Parameters.AddWithValue("$max", max);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var payloadJson = reader.GetString(1);
                var message = JsonSerializer.Deserialize<NodeEventMessage>(payloadJson, PayloadSerializerOptions);
                if (message is not null)
                {
                    results.Add(message);
                }
            }

            return (IReadOnlyList<NodeEventMessage>)results;
        }, cancellationToken).ConfigureAwait(false);
        return messages;
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventIds);
        if (eventIds.Count == 0)
        {
            return;
        }

        var distinctIds = eventIds.Distinct().ToArray();
        await WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            var names = new string[distinctIds.Length];
            for (var i = 0; i < distinctIds.Length; i++)
            {
                var name = $"$id{i}";
                names[i] = name;
                command.Parameters.AddWithValue(name, distinctIds[i]);
            }

            command.CommandText =
                $"DELETE FROM PendingEvents WHERE EventId IN ({string.Join(", ", names)});";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private int _disposed;

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

    private async Task<SqliteConnection> EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_connection is SqliteConnection open)
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
                CREATE TABLE IF NOT EXISTS PendingEvents (
                    EventId TEXT PRIMARY KEY,
                    PayloadJson TEXT NOT NULL,
                    OccurredAtUtcTicks INTEGER NOT NULL,
                    InsertedAtUtcTicks INTEGER NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _connection = connection;
        return connection;
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
