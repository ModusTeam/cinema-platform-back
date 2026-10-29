using Cinema.Domain.Shared;
using MediatR;
using Cinema.Catalog.Application.Movies.Models;

namespace Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Commands;

public record UpdateMovieDetailsCommand(
    Guid Id,
    string? Description,
    int? DurationMinutes,
    double? Rating, 
    int? ReleaseYear,
    IReadOnlyList<Guid>? GenreIds = null,
    string? AgeRestriction = null,
    bool SetAgeRestriction = false,
    IReadOnlyList<ManualCastMember>? Cast = null
) : IRequest<Result<Guid>>;