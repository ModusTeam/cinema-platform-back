using Cinema.Application.Common.Interfaces;
using Cinema.Application.Orders.Dtos;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Orders.Queries.GetMyOrders;

public class GetMyOrdersQueryHandler(
    IApplicationDbContext context,
    ICurrentUserService currentUser) : IRequestHandler<GetMyOrdersQuery, Result<OrderHistoryVm>>
{
    public async Task<Result<OrderHistoryVm>> Handle(GetMyOrdersQuery request, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId == null) 
            return Result.Failure<OrderHistoryVm>(new Error("Auth.Required", "User not authenticated"));

        var now = DateTime.UtcNow;
        
        var activeOrders = await context.Orders
            .AsNoTracking()
            .Where(o => o.UserId == userId)
            .Where(o => o.Session.StartTime > now)
            .Where(o => o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Failed)
            .OrderBy(o => o.Session.StartTime)
            .ProjectToType<OrderDto>() 
            .ToListAsync(ct);
        
        var pastOrders = await context.Orders
            .AsNoTracking()
            .Where(o => o.UserId == userId)
            .Where(o => o.Session.StartTime <= now || o.Status == OrderStatus.Cancelled || o.Status == OrderStatus.Failed)
            .OrderByDescending(o => o.Session.StartTime)
            .ProjectToType<OrderDto>()
            .ToListAsync(ct);

        return Result.Success(new OrderHistoryVm
        {
            ActiveOrders = activeOrders,
            PastOrders = pastOrders
        });
    }
}