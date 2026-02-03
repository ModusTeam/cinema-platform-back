using Cinema.Application.Common.Interfaces;
using Cinema.Application.Genres.Dtos;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Cinema.Application.Genres.Queries.GetGenres;

public record GetGenresQuery : IRequest<List<GenreDto>>;

public class GetGenresQueryHandler(
    IApplicationDbContext context, 
    IMemoryCache cache
) : IRequestHandler<GetGenresQuery, List<GenreDto>>
{
    public async Task<List<GenreDto>> Handle(GetGenresQuery request, CancellationToken ct)
    {
        return await cache.GetOrCreateAsync("all-genres", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);

            return await context.Genres
                .OrderBy(g => g.Name)
                .Select(g => new GenreDto(g.ExternalId, g.Name)) 
                .ToListAsync(ct);
        }) ?? new List<GenreDto>();
    }
}