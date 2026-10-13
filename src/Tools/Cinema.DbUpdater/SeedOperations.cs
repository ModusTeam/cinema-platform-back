using Cinema.Infrastructure.Persistence;

namespace Cinema.DbUpdater;

internal static class SeedOperations
{
    public static Task RunAsync(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
