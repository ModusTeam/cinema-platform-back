using Cinema.Application.Common.Contracts;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Microsoft.EntityFrameworkCore;
namespace Cinema.Infrastructure.Persistence;
public partial class ApplicationDbContext
{
    private void ValidateSessionCompletionWrites()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<SessionCompletionFact>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Session completion history is immutable");
            if (entry.State != EntityState.Added) continue;
            SessionCompletionFact fact = entry.Entity;
            Session? session = ChangeTracker.Entries<Session>().SingleOrDefault(x => x.Entity.Id == fact.SessionId)?.Entity;
            if (session?.Status != SessionStatus.Ended || session.EndTime != fact.CompletedAtUtc ||
                SessionCompletedEnvelope.Create(fact.EventId, fact.SessionId.Value, fact.CompletedAtUtc).ToJson() != fact.Snapshot)
                throw new InvalidOperationException("Session completion requires its source transition");
        }
    }
}
