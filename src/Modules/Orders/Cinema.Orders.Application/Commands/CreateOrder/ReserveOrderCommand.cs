using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Orders.Commands.CreateOrder;

public record ReserveOrderCommand(Guid SessionId, List<Guid> SeatIds) : IRequest<Result<ReservedOrderDto>>;

public record ReservedOrderDto(Guid Id, decimal TotalAmount, string Status);
