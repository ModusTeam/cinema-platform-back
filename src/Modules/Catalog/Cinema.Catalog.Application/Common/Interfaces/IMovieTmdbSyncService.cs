using Cinema.Catalog.Domain.Entities;
using Cinema.Domain.Shared;

namespace Cinema.Catalog.Application.Common.Interfaces;

public interface IMovieTmdbSyncService
{
    Task<Result> ApplyLatestTmdbDetailsAsync(
        Movie movie,
        string? ageRestrictionOverride = null,
        CancellationToken ct = default);
}
