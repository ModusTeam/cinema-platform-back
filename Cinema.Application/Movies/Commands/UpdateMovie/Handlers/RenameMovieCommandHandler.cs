using Cinema.Application.Common.Interfaces;
using Cinema.Application.Movies.Commands.UpdateMovie.Commands;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Movies.Commands.UpdateMovie.Handlers;

public class RenameMovieCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<RenameMovieCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RenameMovieCommand request, CancellationToken ct)
    {
        var movieId = new EntityId<Movie>(request.Id);
        var movie = await context.Movies.FirstOrDefaultAsync(m => m.Id == movieId, ct);
        
        if (movie == null) return Result.Failure<Guid>(new Error("Movie.NotFound", "Movie not found"));

        movie.Rename(request.NewTitle);
        
        await context.SaveChangesAsync(ct);
        return Result.Success(movie.Id.Value);
    }
}