using Cinema.Application.Common.Interfaces;
using Cinema.Application.Orders.Dtos;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Tickets.Queries.GetTicketDetails;

public record GetTicketDetailsQuery(Guid TicketId) : IRequest<Result<TicketDto>>;

public class GetTicketDetailsQueryHandler(
    IApplicationDbContext context,
    ICurrentUserService currentUser)
    : IRequestHandler<GetTicketDetailsQuery, Result<TicketDto>>
{
    public async Task<Result<TicketDto>> Handle(GetTicketDetailsQuery request, CancellationToken ct)
    {
        var ticketId = new EntityId<Ticket>(request.TicketId);
        var userId = currentUser.UserId;

        var ticket = await context.Tickets
            .AsNoTracking()
            .Include(t => t.Order)
            .Include(t => t.Session).ThenInclude(s => s.Movie)
            .Include(t => t.Session).ThenInclude(s => s.Hall)
            .Include(t => t.Seat).ThenInclude(s => s.SeatType)
            .FirstOrDefaultAsync(t => t.Id == ticketId, ct);

        if (ticket == null)
            return Result.Failure<TicketDto>(new Error("Ticket.NotFound", "Ticket not found."));

        if (ticket.Order?.UserId != userId)
            return Result.Failure<TicketDto>(new Error("Ticket.AccessDenied", "This ticket belongs to another user."));

        var dto = ticket.Adapt<TicketDto>();
        
        return Result.Success(dto);
    }
}