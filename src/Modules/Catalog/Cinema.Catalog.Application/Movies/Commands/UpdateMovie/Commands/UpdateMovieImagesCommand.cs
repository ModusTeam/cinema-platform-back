using Cinema.Domain.Shared;
using MediatR;
using Cinema.Catalog.Application.Common.Models.MovieImages;

namespace Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Commands;

public record UpdateMovieImagesCommand(
    Guid Id, 
    string? PosterUrl, 
    string? BackdropUrl, 
    string? TrailerUrl,
    MovieImageReference? Poster = null,
    MovieImageReference? Backdrop = null,
    bool SetPosterReference = false,
    bool SetBackdropReference = false,
    bool SetPosterUrl = false,
    bool SetBackdropUrl = false,
    bool SetTrailerUrl = false
) : IRequest<Result<Guid>>;