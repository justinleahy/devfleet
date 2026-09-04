using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application;
using PiCommandCenter.Infrastructure;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Tests;

public class SystemClockTests
{
    [Fact]
    public void UtcNow_reports_the_machine_clock_in_utc()
    {
        var before = DateTimeOffset.UtcNow;
        var now = new SystemClock().UtcNow;
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(now.UtcTicks, before.UtcTicks, after.UtcTicks);
        Assert.Equal(TimeSpan.Zero, now.Offset);
    }
}

public class ControlPlaneDbContextTests
{
    [Fact]
    public void Context_can_be_created_against_sqlite_and_exposes_identity_schema()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new ControlPlaneDbContext(options);

        Assert.Contains(context.Model.GetEntityTypes(), e => e.ClrType == typeof(Microsoft.AspNetCore.Identity.IdentityUser));
        Assert.IsAssignableFrom<IClock>(new SystemClock());
    }
}
