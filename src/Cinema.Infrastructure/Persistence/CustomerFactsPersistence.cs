using Cinema.Application.Common.Contracts;
using Cinema.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Infrastructure.Persistence;

public partial class ApplicationDbContext
{
    private bool customerFactsScopeFailed;
    public DbSet<CustomerFactRevision> CustomerFactRevisions => Set<CustomerFactRevision>();

    internal void InvalidateCustomerFactsScope()
    {
        customerFactsScopeFailed = true;
        ChangeTracker.Clear();
    }

    private void ValidateCustomerFactsWrites()
    {
        if (customerFactsScopeFailed)
            throw new InvalidOperationException("Discard the failed customer-facts scope");
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<CustomerFactRevision>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Customer fact history is immutable");
            if (entry.State != EntityState.Added) continue;
            CustomerFactRevision fact = entry.Entity;
            CustomerFactsEnvelope snapshot = CustomerFactsEnvelope.FromJson(fact.Snapshot);
            User user = ChangeTracker.Entries<User>().SingleOrDefault(x => x.Entity.Id == fact.UserId)?.Entity
                ?? throw new InvalidOperationException("Source user must share the history transaction");
            if (fact.EventId != snapshot.Data.EventId || fact.UserId != snapshot.Data.UserId || fact.Revision != snapshot.Data.Revision ||
                CustomerFactsEnvelope.FromUser(user, fact.EventId, snapshot.Data.Kind).ToJson() != snapshot.ToJson())
                throw new InvalidOperationException("Source snapshot does not match user state");
        }
        foreach (var entry in ChangeTracker.Entries<User>().Where(x => x.State is EntityState.Modified or EntityState.Added))
        {
            User user = entry.Entity;
            DateTime? registered = entry.State == EntityState.Added ? null : entry.Property(x => x.RegisteredAtUtc).OriginalValue;
            DateTime? first = entry.State == EntityState.Added ? null : entry.Property(x => x.DobFirstSetAtUtc).OriginalValue;
            int revision = entry.State == EntityState.Added ? 0 : entry.Property(x => x.CustomerFactsRevision).OriginalValue;
            if (registered.HasValue && registered != user.RegisteredAtUtc || first.HasValue && first != user.DobFirstSetAtUtc ||
                first.HasValue && entry.Property(x => x.DateOfBirth).OriginalValue?.Date != user.DateOfBirth?.Date)
                throw new InvalidOperationException("Authoritative customer instants and DOB cannot be overwritten");
            bool changed = revision != user.CustomerFactsRevision || registered != user.RegisteredAtUtc || first != user.DobFirstSetAtUtc;
            if (!changed) continue;
            if ((long)user.CustomerFactsRevision != (long)revision + 1 || !ChangeTracker.Entries<CustomerFactRevision>()
                    .Any(x => x.State == EntityState.Added && x.Entity.UserId == user.Id && x.Entity.Revision == user.CustomerFactsRevision))
                throw new InvalidOperationException("Source head must advance with its next immutable revision");
        }
    }
}
