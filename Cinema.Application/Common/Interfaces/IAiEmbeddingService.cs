namespace Cinema.Application.Common.Interfaces;

public interface IAiEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}