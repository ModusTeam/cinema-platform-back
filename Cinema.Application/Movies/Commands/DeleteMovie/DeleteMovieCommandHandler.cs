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
        if (movie == null) 
            return Result.Failure(new Error("Movie.NotFound", "Movie not found."));
        
        var hasActiveSessions = await context.Sessions
            .AnyAsync(s => s.MovieId == movieId && s.EndTime > DateTime.UtcNow, ct);

        if (hasActiveSessions)
        {
            return Result.Failure(new Error("Movie.CannotDelete", 
                "Cannot delete movie because it has active sessions scheduled. Cancel sessions first."));
        }
        
        context.Movies.Remove(movie);
        await context.SaveChangesAsync(ct);

        return Result.Success();
    }
}