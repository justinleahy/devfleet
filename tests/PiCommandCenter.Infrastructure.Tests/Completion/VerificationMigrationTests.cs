using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Tests.Completion;

public class VerificationMigrationTests
{
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
}
