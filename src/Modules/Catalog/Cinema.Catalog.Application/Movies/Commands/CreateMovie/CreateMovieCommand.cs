using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;
using Cinema.Catalog.Application.Common.Models.MovieImages;
using Cinema.Catalog.Application.Movies.Models;

namespace Cinema.Catalog.Application.Movies.Commands.CreateMovie;

public record CreateMovieCommand(
    string Title,
    string Description,
    int DurationMinutes,
    int ReleaseYear,
    MovieStatus Status,
    IReadOnlyList<Guid>? GenreIds = null,
    string? AgeRestriction = null,
    decimal? Rating = null,
    MovieImageReference? Poster = null,
    MovieImageReference? Backdrop = null,
    string? TrailerUrl = null,
    IReadOnlyList<ManualCastMember>? Cast = null
) : IRequest<Result<Guid>>;
