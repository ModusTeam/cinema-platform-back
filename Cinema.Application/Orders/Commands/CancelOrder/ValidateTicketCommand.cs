using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Orders.Commands.CancelOrder;

public record ValidateTicketCommand(Guid TicketId) : IRequest<Result<string>>;

public class ValidateTicketCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<ValidateTicketCommand, Result<string>>
{
    public async Task<Result<string>> Handle(ValidateTicketCommand request, CancellationToken ct)
    {
        var ticketId = new EntityId<Ticket>(request.TicketId);

        var ticket = await context.Tickets
            .Include(t => t.Session)
            .ThenInclude(s => s.Hall)
            .FirstOrDefaultAsync(t => t.Id == ticketId, ct);

        if (ticket == null)
            return Result.Failure<string>(new Error("Ticket.Invalid", "Ticket not found."));

        if (ticket.TicketStatus == TicketStatus.Used)
            return Result.Failure<string>(new Error("Ticket.Used", "Ticket already used!"));

        if (ticket.TicketStatus == TicketStatus.Refunded)
            return Result.Failure<string>(new Error("Ticket.Refunded", "Ticket was refunded. Access denied."));
        
        if (ticket.Session!.StartTime.Date != DateTime.UtcNow.Date)
            return Result.Failure<string>(new Error("Ticket.WrongDate", $"Session is on {ticket.Session!.StartTime}."));

        ticket.MarkAsUsed();
        await context.SaveChangesAsync(ct);

        return Result.Success($"Access Granted. Hall: {ticket.Session!.Hall!.Name}, Seat: {ticket.SeatId}");
    }
}