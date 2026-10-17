using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Orders.Commands.CreateOrder;

public class ReserveOrderCommandHandler(
    IOrderReservationService reservationService,
    ICurrentUserService currentUser,
    ISeatLockingService seatLockingService,
    IApplicationDbContext context) : IRequestHandler<ReserveOrderCommand, Result<ReservedOrderDto>>
{
    public async Task<Result<ReservedOrderDto>> Handle(ReserveOrderCommand request, CancellationToken ct)
    {
        Result<Guid> reservation = await OrderReservationWorkflow.ReserveAsync(
            reservationService, seatLockingService, currentUser.UserId, request.SessionId, request.SeatIds, ct);
        if (reservation.IsFailure)
            return Result.Failure<ReservedOrderDto>(reservation.Error);

        ReservedOrderDto order = await context.Orders.AsNoTracking()
            .Where(candidate => candidate.Id == new EntityId<Order>(reservation.Value))
            .Select(candidate => new ReservedOrderDto(candidate.Id.Value, candidate.TotalAmount, candidate.Status.ToString()))
            .SingleAsync(ct);
        return Result.Success(order);
    }
}
