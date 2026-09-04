using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PiCommandCenter.Infrastructure.Persistence;

/// <summary>
/// Initializes every opened SQLite connection for durable, concurrent access:
/// write-ahead logging, enforced foreign keys, and a busy timeout so simultaneous
/// writers fail on constraints instead of "database is locked".
/// PRAGMAs run after the connection opens — SQLite rejects commands issued
/// mid-open — via the EF Core post-open interception callbacks.
/// </summary>
public sealed class SqliteConnectionInitializerInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData)
    {
        base.ConnectionOpened(connection, eventData);

        if (ShouldSkipPragmas(connection))
        {
            return;
        }

        ExecutePragmas(connection);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);

        if (ShouldSkipPragmas(connection))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = PragmaScript;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string PragmaScript = """
        PRAGMA journal_mode = WAL;
        PRAGMA foreign_keys = ON;
        PRAGMA busy_timeout = 5000;
        PRAGMA synchronous = NORMAL;
        """;

    private static bool ShouldSkipPragmas(DbConnection connection)
    {
        return connection is SqliteConnection sqlite
            && sqlite.ConnectionString is not null
            && new SqliteConnectionStringBuilder(sqlite.ConnectionString).Mode == SqliteOpenMode.ReadOnly;
    }

    private static void ExecutePragmas(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PragmaScript;
        command.ExecuteNonQuery();
    }
}
