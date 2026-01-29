using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Infrastructure.Services;

public class EfMovieInfoProvider(IApplicationDbContext context) : IMovieInfoProvider
{
    public async Task<int?> GetDurationMinutesAsync(EntityId<Movie> movieId, CancellationToken ct = default)
    {
        return await context.Movies
            .AsNoTracking()
            .Where(m => m.Id == movieId)
            .Select(m => (int?)m.DurationMinutes)
            .FirstOrDefaultAsync(ct);
    }
}