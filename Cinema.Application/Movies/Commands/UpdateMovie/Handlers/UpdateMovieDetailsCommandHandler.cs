using Cinema.Application.Common.Interfaces;
using Cinema.Application.Movies.Commands.UpdateMovie.Commands;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Movies.Commands.UpdateMovie.Handlers;

public class UpdateMovieDetailsCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<UpdateMovieDetailsCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(UpdateMovieDetailsCommand request, CancellationToken ct)
    {
        var movieId = new EntityId<Movie>(request.Id);
        var movie = await context.Movies.FirstOrDefaultAsync(m => m.Id == movieId, ct);
        
        if (movie == null) return Result.Failure<Guid>(new Error("Movie.NotFound", "Movie not found"));
        
        decimal? ratingDecimal = request.Rating.HasValue ? (decimal)request.Rating.Value : null;

        movie.UpdateSpecs(
            request.Description, 
            request.DurationMinutes, 
            ratingDecimal,
            request.ReleaseYear
        );

        await context.SaveChangesAsync(ct);
        return Result.Success(movie.Id.Value);
    }
}