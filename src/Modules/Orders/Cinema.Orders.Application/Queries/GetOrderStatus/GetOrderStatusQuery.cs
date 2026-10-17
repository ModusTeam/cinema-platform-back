using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Orders.Queries.GetOrderStatus;

public record GetOrderStatusQuery(Guid OrderId) : IRequest<Result<OrderStatusDto>>;

public record OrderStatusDto(Guid Id, string Status);

public class GetOrderStatusQueryHandler(
    IApplicationDbContext context,
    ICurrentUserService currentUser) : IRequestHandler<GetOrderStatusQuery, Result<OrderStatusDto>>
{
    public async Task<Result<OrderStatusDto>> Handle(GetOrderStatusQuery request, CancellationToken ct)
    {
        Guid? userId = currentUser.UserId;
        if (userId is null)
            return Result.Failure<OrderStatusDto>(new Error("Auth.Required", "User not authenticated"));

        OrderStatusDto? order = await context.Orders.AsNoTracking()
            .Where(candidate => candidate.Id == new EntityId<Order>(request.OrderId) && candidate.UserId == userId)
            .Select(candidate => new OrderStatusDto(candidate.Id.Value, candidate.Status.ToString()))
            .SingleOrDefaultAsync(ct);
        return order is null
            ? Result.Failure<OrderStatusDto>(new Error("Order.NotFound", "Order not found or unauthorized."))
            : Result.Success(order);
    }
}
