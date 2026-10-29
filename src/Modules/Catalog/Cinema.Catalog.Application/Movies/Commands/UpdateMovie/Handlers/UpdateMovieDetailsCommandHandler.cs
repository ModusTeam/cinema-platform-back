using Cinema.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Commands;
using Cinema.Domain.Common;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Errors;
using Cinema.Domain.Shared;
using MediatR;
using Cinema.Catalog.Application.Movies.Services;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Handlers;

public class UpdateMovieDetailsCommandHandler(IApplicationDbContext context, ManualMovieReferences references)
    : IRequestHandler<UpdateMovieDetailsCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(UpdateMovieDetailsCommand request, CancellationToken ct)
    {
        var movieId = new EntityId<Movie>(request.Id);
        var movie = await context.Movies.Include(m => m.MovieGenres).FirstOrDefaultAsync(m => m.Id == movieId, ct);

        if (movie is null) return Result.Failure<Guid>(MovieErrors.NotFound);

        var genres = await references.ResolveGenresAsync(request.GenreIds, ct);
        if (genres.IsFailure) return Result.Failure<Guid>(genres.Error);

        decimal? ratingDecimal = request.Rating.HasValue ? (decimal)request.Rating.Value : null;

        movie.UpdateSpecs(
            request.Description,
            request.DurationMinutes,
            ratingDecimal,
            request.ReleaseYear
        );

        if (request.GenreIds is not null)
            movie.ReplaceGenres(genres.Value);
        if (request.SetAgeRestriction || request.AgeRestriction is not null) movie.SetAgeRestriction(request.AgeRestriction);
        if (request.Cast is not null)
            movie.SetCast(request.Cast.Select(member => new MovieCastMember { Name = member.Name.Trim(), Role = member.Role?.Trim() }));

        await context.SaveChangesAsync(ct);
        return Result.Success(movie.Id.Value);
    }
}