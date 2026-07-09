using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RbRules.Infrastructure;

/// <summary>Voor `dotnet ef migrations add` — geen draaiende DB nodig.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<RbRulesDbContext>
{
    public RbRulesDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("DATABASE_URL")
                 ?? "Host=localhost;Database=rbrules;Username=rbrules;Password=rbrules";
        var options = new DbContextOptionsBuilder<RbRulesDbContext>()
            .UseNpgsql(cs, o => o.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new RbRulesDbContext(options);
    }
}
