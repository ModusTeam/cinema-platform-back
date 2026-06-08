using Cinema.Domain.Entities;
using Cinema.Domain.Shared;

namespace Cinema.Application.Common.Interfaces;

public interface IMovieTmdbSyncService
{
    Task<Result> ApplyLatestTmdbDetailsAsync(
        Movie movie,
        string? ageRestrictionOverride = null,
        CancellationToken ct = default);
}
