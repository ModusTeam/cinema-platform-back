using Cinema.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cinema.Application.Orders.Services;

// Nested EF operations must not reconnect and retry after losing a session lock.
// Retry the durable journal from a fresh worker instead of replaying checkout.
internal sealed class CheckoutExecutionStrategy : ExecutionStrategy
{
    public CheckoutExecutionStrategy(IApplicationDbContext context)
        : base(context.Database.GetService<ExecutionStrategyDependencies>(), 0, TimeSpan.Zero)
    {
        if (Current is { RetriesOnFailure: true })
            throw new InvalidOperationException("Checkout cannot run inside a retried EF operation.");
    }

    protected override bool ShouldRetryOn(Exception exception) => false;
}
