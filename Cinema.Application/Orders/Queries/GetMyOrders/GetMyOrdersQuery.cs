using Cinema.Application.Common.Interfaces;
using Cinema.Application.Orders.Dtos;
using Cinema.Domain.Shared;
using Mapster; // ✅ Додано
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Orders.Queries.GetMyOrders;

public record GetMyOrdersQuery : IRequest<Result<MyOrdersVm>>;

public class GetMyOrdersQueryHandler(
    IApplicationDbContext context,
    ICurrentUserService currentUser) 
    : IRequestHandler<GetMyOrdersQuery, Result<MyOrdersVm>>
{
    public async Task<Result<MyOrdersVm>> Handle(GetMyOrdersQuery request, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId == null) return Result.Failure<MyOrdersVm>(new Error("Auth.Required", "User not found"));
        
        var allOrders = await context.Orders
            .AsNoTracking()
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.BookingDate)
            .ProjectToType<OrderDto>() 
            .ToListAsync(ct);

        var active = allOrders.Where(o => 
            o.Status == "Paid" && 
            o.Tickets.Any(t => t.SessionStart > DateTime.UtcNow && t.Status == "Valid")
        ).ToList();

        var past = allOrders.Except(active).ToList();

        return Result.Success(new MyOrdersVm(active, past));
    }
}