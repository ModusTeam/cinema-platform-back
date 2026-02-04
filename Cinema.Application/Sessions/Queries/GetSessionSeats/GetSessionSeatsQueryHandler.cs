using Cinema.Application.Common.Interfaces;
using Cinema.Application.Sessions.Queries.GetSessionSeats; // Перевір namespace DTO
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Queries.GetSessionSeats;

public class GetSessionSeatsQueryHandler(
    IApplicationDbContext context,
    IPriceCalculator priceCalculator,
    ISeatLockingService seatLockingService,
    ISeatTypeProvider seatTypeProvider,
    ICurrentUserService currentUser)
    : IRequestHandler<GetSessionSeatsQuery, Result<SessionSeatsVm>>
{
    public async Task<Result<SessionSeatsVm>> Handle(GetSessionSeatsQuery request, CancellationToken ct)
    {
        var sessionId = new EntityId<Session>(request.SessionId);
        
        var session = await context.Sessions
            .AsNoTracking()
            .Include(s => s.Movie)
            .Include(s => s.Hall)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session == null)
            return Result.Failure<SessionSeatsVm>(new Error("Session.NotFound", "Session not found"));
        
        var seats = await context.Seats
            .AsNoTracking()
            .Where(s => s.HallId == session.HallId && s.Status == SeatStatus.Active)
            .OrderBy(s => s.RowLabel).ThenBy(s => s.Number)
            .ToListAsync(ct);
        
        var soldSeatIds = await context.Tickets
             .AsNoTracking()
             .Where(t => t.SessionId == sessionId && 
                         (t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used))
             .Select(t => t.SeatId)
             .ToListAsync(ct);
        var soldSeatIdsSet = soldSeatIds.ToHashSet();
        
        var availableSeatGuids = seats
            .Where(s => !soldSeatIdsSet.Contains(s.Id))
            .Select(s => s.Id.Value)
            .ToList();
        var lockedSeatIds = await seatLockingService.GetLockedSeatsAsync(request.SessionId, availableSeatGuids, ct);
        var lockedSeatIdsSet = lockedSeatIds.ToHashSet();
        
        var pricing = await context.Pricings
            .Include(p => p.PricingItems)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == session.PricingId, ct);

        if (pricing == null)
            return Result.Failure<SessionSeatsVm>(new Error("Pricing.NotFound", "Pricing not configured"));

        var seatDtos = new List<SeatDto>(seats.Count);
        var priceCache = new Dictionary<EntityId<SeatType>, decimal>();

        foreach (var seat in seats)
        {
            if (!priceCache.TryGetValue(seat.SeatTypeId, out var price))
            {
                price = priceCalculator.CalculatePrice(pricing, seat.SeatTypeId, session.StartTime);
                priceCache[seat.SeatTypeId] = price;
            }

            var isSold = soldSeatIdsSet.Contains(seat.Id);
            var isLocked = lockedSeatIdsSet.Contains(seat.Id.Value);
            
            var typeName = seatTypeProvider.GetName(seat.SeatTypeId);

            seatDtos.Add(new SeatDto(
                seat.Id.Value,
                seat.GridX,
                seat.Number,
                seat.RowLabel,
                seat.GridX,
                seat.GridY,
                typeName,
                price,
                IsAvailable: !isSold && !isLocked,
                IsSold: isSold,
                IsLocked: isLocked
            ));
        }
        
        var vm = new SessionSeatsVm(
            session.Id.Value,
            session.HallId.Value,
            session.Movie?.Title ?? "Unknown Movie",
            session.StartTime,
            seatDtos
        );

        return Result.Success(vm);
    }
}