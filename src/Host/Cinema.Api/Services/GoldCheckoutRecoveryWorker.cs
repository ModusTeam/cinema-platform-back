using Cinema.Application.Common.Interfaces;

namespace Cinema.Api.Services;

public sealed class GoldCheckoutRecoveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<GoldCheckoutRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IOrderCheckoutOrchestrator>()
                    .RecoverGoldCheckoutsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GOLD checkout recovery scan failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
