using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Movies.Commands.DeleteMovie;

public class DeleteMovieCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<DeleteMovieCommand, Result>
{
    public async Task<Result> Handle(DeleteMovieCommand request, CancellationToken ct)
    {
        var movieId = new EntityId<Movie>(request.Id);
        var movie = await context.Movies.FirstOrDefaultAsync(m => m.Id == movieId, ct);

        if (movie == null) return Result.Failure(new Error("Movie.NotFound", "Movie not found"));

        movie.Delete(); 
        await context.SaveChangesAsync(ct);

        return Result.Success();
    }
}