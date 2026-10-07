using Cinema.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Errors;
using Cinema.Domain.Shared;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Catalog.Application.Movies.Commands.RestoreMovie;

public class RestoreMovieCommandHandler(
    IApplicationDbContext context,
    IMovieTmdbSyncService tmdbSyncService,
    IBackgroundJobClient jobClient) : IRequestHandler<RestoreMovieCommand, Result>
{
    public async Task<Result> Handle(RestoreMovieCommand request, CancellationToken ct)
    {
        var movieId = new EntityId<Movie>(request.Id);
        var movie = await context.Movies
            .Include(m => m.MovieGenres)
            .FirstOrDefaultAsync(m => m.Id == movieId, ct);

        if (movie is null) return Result.Failure(MovieErrors.NotFound);
        if (!movie.IsDeleted) return Result.Failure(MovieErrors.AlreadyActive);

        movie.Restore();

        if (movie.ExternalId.HasValue)
        {
            var syncResult = await tmdbSyncService.ApplyLatestTmdbDetailsAsync(movie, ct: ct);
            if (syncResult.IsFailure) return syncResult;
        }

        await context.SaveChangesAsync(ct);

        jobClient.Enqueue<IAiEmbeddingService>(s =>
            s.UpdateMovieEmbeddingAsync(movie.Id.Value, CancellationToken.None));

        return Result.Success();
    }
}
