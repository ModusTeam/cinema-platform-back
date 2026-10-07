using Cinema.Domain.Shared;

namespace Cinema.Application.Common.Interfaces;

public interface IAiEmbeddingService
{
    Task<Result<float[]>> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
    Task UpdateMovieEmbeddingAsync(Guid movieId, CancellationToken ct);
}