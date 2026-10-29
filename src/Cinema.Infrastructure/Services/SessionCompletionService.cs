using Cinema.Application.Common.Contracts;
using Cinema.Application.Services;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
namespace Cinema.Infrastructure.Services;
// Scoped ISendEndpointProvider is the EF Bus Outbox (UseBusOutbox), not IBus.
// Send stages an OutboxMessage in this same DbContext; broker I/O is deferred.
public sealed class SessionCompletionService(ApplicationDbContext context, ISendEndpointProvider endpoints)
{
    public Task<bool> CompleteAsync(Guid sessionId, CancellationToken ct)
        => SessionLifecycleTransaction.ExecuteAsync(context, sessionId, async (session, token) =>
        {
            if (session is null) return false;
            SessionCompletionFact? existing = await context.Set<SessionCompletionFact>()
                .SingleOrDefaultAsync(x => x.SessionId == new EntityId<Session>(sessionId), token);
            if (session.Status == SessionStatus.Ended)
            {
                if (existing is null) throw new InvalidOperationException("SESSION_COMPLETION_SOURCE_MISSING");
                return false;
            }
            if (existing is not null) throw new InvalidOperationException("SESSION_COMPLETION_SOURCE_CONFLICT");
            DateTime cutoff = await context.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
            if (!session.TryComplete(cutoff)) return false;
            DateTime end = session.EndTime!.Value;
            Guid eventId = Guid.NewGuid();
            SessionCompletedEnvelope envelope = SessionCompletedEnvelope.Create(eventId, sessionId, end);
            context.Set<SessionCompletionFact>().Add(SessionCompletionFact.Create(eventId, session.Id, end, envelope.ToJson()));
            ISendEndpoint endpoint = await endpoints.GetSendEndpoint(new Uri("queue:loyalty_ticket_purchased"));
            await endpoint.Send(envelope, send => send.MessageId = eventId, token);
            await context.SaveChangesAsync(token);
            return true;
        }, ct, includeInactive: true);
}
