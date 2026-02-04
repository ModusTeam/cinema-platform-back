using Cinema.Application.Common.Interfaces;
using Cinema.Application.Orders.Dtos;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Orders.Queries.GetUserOrders;

public record GetUserOrdersQuery(Guid UserId) : IRequest<Result<List<OrderDto>>>;

public class GetUserOrdersQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetUserOrdersQuery, Result<List<OrderDto>>>
{
    public async Task<Result<List<OrderDto>>> Handle(GetUserOrdersQuery request, CancellationToken ct)
    {
        var orders = await context.Orders
            .AsNoTracking()
            .Where(o => o.UserId == request.UserId)
            .OrderByDescending(o => o.BookingDate)
            .ProjectToType<OrderDto>() 
            .ToListAsync(ct);

        return Result.Success(orders);
    }
}