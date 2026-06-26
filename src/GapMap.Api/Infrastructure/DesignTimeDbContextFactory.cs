using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace GapMap.Api.Infrastructure;

// Lets `dotnet ef migrations` / `database update` build the context at design time
// without running Program.cs. Reads the same connection string sources.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GapMapDbContext>
{
    public GapMapDbContext CreateDbContext(string[] args)
    {
        var cfg = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddUserSecrets(typeof(DesignTimeDbContextFactory).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var conn = cfg.GetConnectionString("Postgres")
                   ?? "Host=localhost;Port=5432;Database=gapmap;Username=gapmap;Password=CHANGE_ME";

        var options = new DbContextOptionsBuilder<GapMapDbContext>().UseNpgsql(conn).Options;
        return new GapMapDbContext(options);
    }
}
