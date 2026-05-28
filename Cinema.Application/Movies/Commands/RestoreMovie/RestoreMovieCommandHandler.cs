using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Errors;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Movies.Commands.RestoreMovie;

public class RestoreMovieCommandHandler(IApplicationDbContext context)
    : IRequestHandler<RestoreMovieCommand, Result>
{
    public async Task<Result> Handle(RestoreMovieCommand request, CancellationToken ct)
    {
        var movieId = new EntityId<Movie>(request.Id);
        var movie = await context.Movies.FirstOrDefaultAsync(m => m.Id == movieId, ct);

        if (movie is null) return Result.Failure(MovieErrors.NotFound);
        if (!movie.IsDeleted) return Result.Failure(MovieErrors.AlreadyActive);

        movie.Restore();
        await context.SaveChangesAsync(ct);

        return Result.Success();
    }
}
