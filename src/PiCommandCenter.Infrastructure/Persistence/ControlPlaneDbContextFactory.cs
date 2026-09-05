using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Persistence;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> works without a startup project
/// (<c>dotnet ef migrations add ... --project src/PiCommandCenter.Infrastructure</c>).
/// </summary>
public sealed class ControlPlaneDbContextFactory : IDesignTimeDbContextFactory<ControlPlaneDbContext>
{
    public ControlPlaneDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlite("Data Source=designtime-controlplane.db")
            .Options;
        return new ControlPlaneDbContext(options);
    }
}
