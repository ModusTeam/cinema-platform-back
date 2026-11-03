using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Data.Common;

namespace Cinema.Application.Orders.Commands.CancelOrder;

public record ValidateTicketCommand(Guid TicketId) : IRequest<Result<string>>;

public class ValidateTicketCommandHandler(IApplicationDbContext context, TimeProvider? timeProvider = null)
    : IRequestHandler<ValidateTicketCommand, Result<string>>
{
    public async Task<Result<string>> Handle(ValidateTicketCommand request, CancellationToken ct)
    {
        if (!context.Database.IsRelational()) return await ValidateAsync(request, ct);
        if (context.Database.CurrentTransaction is not null) throw new InvalidOperationException("Fresh ticket validation scope required.");
        try
        {
            return await new ValidationStrategy(context).ExecuteAsync(async () =>
            {
                var source = await context.Tickets.AsNoTracking().Where(t => t.Id == new EntityId<Ticket>(request.TicketId))
                    .Select(t => new { t.OrderId, t.SessionId }).SingleOrDefaultAsync(ct);
                if (source is null) return Result.Failure<string>(new Error("Ticket.Invalid", "Ticket not found."));
                await using var transaction = await context.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '3s'", ct);
                // Same arbitration as ordinary admission; never lock Hall or Seat here.
                await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM sessions WHERE id={source.SessionId.Value} FOR SHARE", ct);
                await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM orders WHERE id={source.OrderId.Value} FOR UPDATE", ct);
                await context.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM tickets WHERE id={request.TicketId} FOR UPDATE", ct);
                foreach (var entry in context.ChangeTracker.Entries().Where(e => e.Entity switch
                {
                    Ticket t => t.Id.Value == request.TicketId,
                    Order o => o.Id == source.OrderId,
                    Session s => s.Id == source.SessionId,
                    _ => false
                }).ToList()) await entry.ReloadAsync(ct);
                Result<string> result = await ValidateAsync(request, ct, source.OrderId.Value, source.SessionId.Value);
                await transaction.CommitAsync(ct);
                return result;
            });
        }
        catch (Exception exception) when (exception is DbException { SqlState: "55P03" } || exception.InnerException is DbException { SqlState: "55P03" })
        {
            context.ClearChangeTracker();
            return Result.Failure<string>(new Error("Ticket.Busy", "Ticket lifecycle is being modified. Retry later."));
        }
        catch { context.ClearChangeTracker(); throw; }
    }

    private async Task<Result<string>> ValidateAsync(ValidateTicketCommand request, CancellationToken ct, Guid? expectedOrder = null, Guid? expectedSession = null)
    {
        var ticketId = new EntityId<Ticket>(request.TicketId);

        var ticket = await context.Tickets
            .Include(t => t.Session!)
            .ThenInclude(s => s.Hall)
            .FirstOrDefaultAsync(t => t.Id == ticketId, ct);

        if (ticket == null)
            return Result.Failure<string>(new Error("Ticket.Invalid", "Ticket not found."));
        if (expectedOrder is not null && (ticket.OrderId.Value != expectedOrder || ticket.SessionId.Value != expectedSession))
            return Result.Failure<string>(new Error("Ticket.Invalid", "Ticket ownership changed. Retry from a fresh scope."));

        if (ticket.TicketStatus == TicketStatus.Used)
            return Result.Failure<string>(new Error("Ticket.Used", "Ticket already used!"));

        if (ticket.TicketStatus == TicketStatus.Refunded)
            return Result.Failure<string>(new Error("Ticket.Refunded", "Ticket was refunded. Access denied."));

        if (ticket.TicketStatus is TicketStatus.Reserved or TicketStatus.Released)
            return Result.Failure<string>(new Error("Ticket.NotPurchased", "Ticket has not been purchased."));

        if (!await context.Orders.AsNoTracking().AnyAsync(
                order => order.Id == ticket.OrderId && order.Status == OrderStatus.Paid, ct))
            return Result.Failure<string>(new Error("Ticket.NotPurchased", "Ticket has not been purchased."));

        // Every durable operation owns the lifecycle until an explicit resolution releases it.
        // Even Declined has no approved ticket-reopening protocol and may have late conflicting evidence.
        if (await context.RefundOperations.AsNoTracking().AnyAsync(x => x.OrderId == ticket.OrderId, ct))
            return Result.Failure<string>(new Error("Ticket.RefundResolutionRequired", "Refund resolution is required before ticket use."));
        
        if (ticket.Session!.StartTime.Date != (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime.Date)
            return Result.Failure<string>(new Error("Ticket.WrongDate", $"Session is on {ticket.Session!.StartTime}."));

        ticket.MarkAsUsed();
        await context.SaveChangesAsync(ct);

        return Result.Success($"Access Granted. Hall: {ticket.Session!.Hall!.Name}, Seat: {ticket.SeatId}");
    }

    private sealed class ValidationStrategy : ExecutionStrategy
    {
        public ValidationStrategy(IApplicationDbContext db)
            : base(db.Database.GetService<ExecutionStrategyDependencies>(), 0, TimeSpan.Zero)
        {
            if (Current is { RetriesOnFailure: true }) throw new InvalidOperationException("Ticket validation cannot run inside a retried EF operation.");
        }
        protected override bool ShouldRetryOn(Exception exception) => false;
    }
}
