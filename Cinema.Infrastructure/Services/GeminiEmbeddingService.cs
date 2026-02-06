using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Cinema.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Cinema.Infrastructure.Services;

public class GeminiEmbeddingService(HttpClient httpClient, IConfiguration configuration) : IAiEmbeddingService
{
    private readonly string _apiKey = configuration["Gemini:ApiKey"]!;
    private readonly string _modelId = configuration["Gemini:EmbeddingModelId"] ?? "text-embedding-004";

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_modelId}:embedContent?key={_apiKey}";

        var requestBody = new
        {
            model = $"models/{_modelId}",
            content = new
            {
                parts = new[]
                {
                    new { text = text }
                }
            },
            outputDimensionality = 768
        };

        var response = await httpClient.PostAsJsonAsync(url, requestBody, ct);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new Exception($"Gemini API Error: {response.StatusCode}, Details: {error}");
        }

        var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: ct);
        
        return result?.Embedding?.Values ?? Array.Empty<float>();
    }
    
    private class GeminiResponse
    {
        [JsonPropertyName("embedding")]
        public EmbeddingData? Embedding { get; set; }
    }

    private class EmbeddingData
    {
        [JsonPropertyName("values")]
        public float[]? Values { get; set; }
    }
}
