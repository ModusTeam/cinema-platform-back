using Cinema.Application.Common.Interfaces;
using Cinema.Application.Halls.Dtos;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Halls.Queries.GetHallById;

public class GetHallByIdQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetHallByIdQuery, Result<HallDto>>
{
    public async Task<Result<HallDto>> Handle(GetHallByIdQuery request, CancellationToken cancellationToken)
    {
        var hallId = new EntityId<Hall>(request.Id);
        var hallDto = await context.Halls
            .AsNoTracking()
            .Where(h => h.Id == hallId)
            .ProjectToType<HallDto>() 
            .FirstOrDefaultAsync(cancellationToken);

        if (hallDto == null)
        {
            return Result.Failure<HallDto>(new Error("Hall.NotFound", "Hall not found"));
        }
        
        return Result.Success(hallDto);
    }
}