using Cinema.Application.Common.Models.Gemini;
using Refit;

namespace Cinema.Application.Common.Interfaces;

public interface IGeminiApi
{
    [Post("/v1beta/models/{modelId}:embedContent?key={apiKey}")]
    Task<GeminiResponse> GenerateEmbeddingAsync(string modelId, string apiKey, [Body] GeminiRequest request);
}