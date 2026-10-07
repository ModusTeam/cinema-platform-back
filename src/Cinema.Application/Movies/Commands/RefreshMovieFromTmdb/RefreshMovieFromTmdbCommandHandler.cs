using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Errors;
using Cinema.Domain.Shared;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Movies.Commands.RefreshMovieFromTmdb;

public class RefreshMovieFromTmdbCommandHandler(
    IApplicationDbContext context,
    IMovieTmdbSyncService tmdbSyncService,
    IBackgroundJobClient jobClient) : IRequestHandler<RefreshMovieFromTmdbCommand, Result>
{
    public async Task<Result> Handle(RefreshMovieFromTmdbCommand request, CancellationToken ct)
    {
        var movieId = new EntityId<Movie>(request.Id);
        var movie = await context.Movies
            .Include(m => m.MovieGenres)
            .FirstOrDefaultAsync(m => m.Id == movieId, ct);

        if (movie is null) return Result.Failure(MovieErrors.NotFound);

        var syncResult = await tmdbSyncService.ApplyLatestTmdbDetailsAsync(movie, ct: ct);
        if (syncResult.IsFailure) return syncResult;

        await context.SaveChangesAsync(ct);

        jobClient.Enqueue<IAiEmbeddingService>(s =>
            s.UpdateMovieEmbeddingAsync(movie.Id.Value, CancellationToken.None));

        return Result.Success();
    }
}
