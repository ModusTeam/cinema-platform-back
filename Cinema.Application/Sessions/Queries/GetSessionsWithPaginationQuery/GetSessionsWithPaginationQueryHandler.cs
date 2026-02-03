using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Mappings;
using Cinema.Application.Sessions.Dtos;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Queries.GetSessionsWithPaginationQuery;

public class GetSessionsWithPaginationQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetSessionsWithPaginationQuery, Result<PaginatedList<SessionDto>>>
{
    public async Task<Result<PaginatedList<SessionDto>>> Handle(GetSessionsWithPaginationQuery request, CancellationToken cancellationToken)
    {
        var paginatedList = await context.Sessions
            .AsNoTracking()
            .OrderByDescending(x => x.StartTime)
            .ProjectToType<SessionDto>()
            .PaginatedListAsync(request.PageNumber, request.PageSize);

        return Result.Success(paginatedList);
    }
}