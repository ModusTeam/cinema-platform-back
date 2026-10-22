using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cinema.SchedulingTests;

public class SchedulingModelTests
{
    [Fact]
    public void Current_model_matches_existing_migration_snapshot()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=model_check", postgres => postgres.UseVector())
            .UseSnakeCaseNamingConvention().Options;
        using ApplicationDbContext context = new(options);

        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
