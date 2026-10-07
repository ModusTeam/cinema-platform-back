using Cinema.Domain.Common;
using Cinema.Domain.Entities;

namespace Cinema.Domain.Interfaces;

public interface IMovieInfoProvider
{
    Task<int?> GetDurationMinutesAsync(EntityId<Movie> movieId, CancellationToken ct = default);
}