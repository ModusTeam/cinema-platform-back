using Cinema.Application.Common.Contracts;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cinema.Infrastructure.Services;

// Foundation only: callers must check Identity results and perform DB work only.
// No registration/DOB handler invokes this writer until the next approved slice.
public sealed class CustomerFactsWriter(ApplicationDbContext context, ISendEndpointProvider endpoints)
{
    private Guid? activeUser;
    private bool failed;

    public async Task<Result> ExecuteAsync(Guid userId, Func<CancellationToken, Task<Result>> mutation, CancellationToken ct)
    {
        if (failed || activeUser.HasValue || context.Database.CurrentTransaction is not null ||
            ExecutionStrategy.Current is { RetriesOnFailure: true })
            throw new InvalidOperationException("A fresh, non-retrying customer-facts scope is required");
        try
        {
            return await new CustomerFactsExecutionStrategy(context).ExecuteAsync(async () =>
            {
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(ct);
                activeUser = userId;
                // A missing row is a new account; its PK arbitrates concurrent insertion.
                await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM \"AspNetUsers\" WHERE id = {userId} FOR UPDATE", ct);
                var tracked = context.ChangeTracker.Entries<User>().SingleOrDefault(x => x.Entity.Id == userId);
                if (tracked is not null) await tracked.ReloadAsync(ct);
                Result result = await mutation(ct);
                if (result.IsFailure)
                {
                    failed = true;
                    context.InvalidateCustomerFactsScope();
                    return result; // Transaction disposal rolls back intermediate Identity saves.
                }
                await context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return result;
            });
        }
        catch
        {
            // Rollback and ambiguous commit both prohibit later saves in this scope.
            failed = true;
            context.InvalidateCustomerFactsScope();
            throw;
        }
        finally
        {
            activeUser = null;
        }
    }

    public async Task StageAsync(User user, Guid eventId, string kind, CancellationToken ct)
    {
        if (failed || activeUser != user.Id || context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Customer fact staging requires its active user transaction");
        CustomerFactsEnvelope envelope = CustomerFactsEnvelope.FromUser(user, eventId, kind);
        context.CustomerFactRevisions.Add(CustomerFactRevision.Create(eventId, user.Id, user.CustomerFactsRevision, envelope.ToJson()));
        ISendEndpoint endpoint = await endpoints.GetSendEndpoint(new Uri("queue:loyalty_ticket_purchased"));
        await endpoint.Send(envelope, send => send.MessageId = eventId, ct);
    }

    private sealed class CustomerFactsExecutionStrategy(ApplicationDbContext db)
        : ExecutionStrategy(db.Database.GetService<ExecutionStrategyDependencies>(), 0, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => false;
    }
}
