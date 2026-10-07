using Cinema.Domain.Shared;

namespace Cinema.Catalog.Application.Common.Interfaces;

public interface IAiEmbeddingService
{
    Task<Result<float[]>> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
    Task UpdateMovieEmbeddingAsync(Guid movieId, CancellationToken ct);
}