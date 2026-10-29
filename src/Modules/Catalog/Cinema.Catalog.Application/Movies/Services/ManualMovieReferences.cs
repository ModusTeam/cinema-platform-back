using Cinema.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Models.MovieImages;
using Cinema.Catalog.Domain.Entities;
using Cinema.Domain.Common;
using Cinema.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Catalog.Application.Movies.Services;

public sealed class ManualMovieReferences(IApplicationDbContext context, IMovieImageStorage images)
{
    public async Task<Result<List<Genre>>> ResolveGenresAsync(IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        if (ids is null or { Count: 0 }) return Result.Success(new List<Genre>());
        if (ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Count)
            return Result.Failure<List<Genre>>(new Error("Movie.Validation.Genres", "Genre IDs must be non-empty and unique."));
        var genreIds = ids.Select(id => new EntityId<Genre>(id)).ToArray();
        List<Genre> genres = await context.Genres.Where(genre => genreIds.Contains(genre.Id)).ToListAsync(ct);
        return genres.Count == ids.Count
            ? Result.Success(genres)
            : Result.Failure<List<Genre>>(new Error("Genre.NotFound", "One or more selected genres do not exist."));
    }

    public async Task<Result> ValidateImagesAsync(MovieImageReference? poster, MovieImageReference? backdrop, CancellationToken ct)
    {
        if (poster is not null)
        {
            Result result = await images.ValidateReferenceAsync(poster, MovieImageKind.Poster, ct);
            if (result.IsFailure) return result;
        }
        return backdrop is null ? Result.Success() : await images.ValidateReferenceAsync(backdrop, MovieImageKind.Backdrop, ct);
    }
}
