using Cinema.Catalog.Application.Common.Models.MovieImages;
using Cinema.Domain.Shared;

namespace Cinema.Catalog.Application.Common.Interfaces;

public interface IMovieImageStorage
{
    Task<Result<MovieImageUploadResult>> UploadAsync(MovieImageUpload image, CancellationToken ct);
    Task<Result> ValidateReferenceAsync(MovieImageReference image, MovieImageKind kind, CancellationToken ct);
}
