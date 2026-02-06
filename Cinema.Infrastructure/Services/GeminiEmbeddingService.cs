using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinema.Infrastructure.Services;

public class GeminiEmbeddingService(
    HttpClient httpClient, 
    IOptions<GeminiOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<GeminiEmbeddingService> logger) : IAiEmbeddingService
{
    private readonly GeminiOptions _settings = options.Value;

    public async Task<Result<float[]>> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var requestUrl = $"/v1beta/models/{_settings.EmbeddingModelId}:embedContent?key={_settings.ApiKey}";

        var requestBody = new
        {
            model = $"models/{_settings.EmbeddingModelId}",
            content = new { parts = new[] { new { text } } },
            outputDimensionality = 768
        };

        try
        {
            var response = await httpClient.PostAsJsonAsync(requestUrl, requestBody, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                logger.LogError("Gemini API Error. Status: {Status}, Details: {Details}", response.StatusCode, errorContent);
                
                return Result.Failure<float[]>(new Error(
                    "Gemini.ApiError", 
                    $"External API call failed with status {response.StatusCode}"));
            }

            var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: ct);
            
            if (result?.Embedding?.Values is null)
            {
                logger.LogWarning("Gemini API returned success but no embedding data.");
                return Result.Failure<float[]>(new Error("Gemini.EmptyResponse", "API returned empty embedding"));
            }

            return Result.Success(result.Embedding.Values);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during Gemini embedding generation");
            return Result.Failure<float[]>(new Error("Gemini.Exception", "An error occurred while calling AI service"));
        }
    }

    public async Task UpdateMovieEmbeddingAsync(Guid movieId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        
        var movie = await context.Movies.FindAsync(new object[] { new EntityId<Movie>(movieId) }, ct);
        
        if (movie == null)
        {
            logger.LogWarning("Movie with ID {MovieId} not found for embedding update.", movieId);
            return;
        }
        
        var text = $"Movie: {movie.Title}. Description: {movie.Description}";
        var embeddingResult = await GenerateEmbeddingAsync(text, ct);
        
        if (embeddingResult.IsFailure)
        {
            logger.LogError("Failed to update embedding for movie {MovieId}. Error: {Error}", movieId, embeddingResult.Error);
            return;
        }
        
        movie.SetEmbedding(embeddingResult.Value);
        await context.SaveChangesAsync(ct);
        
        logger.LogInformation("Successfully updated embedding for movie {MovieId}", movieId);
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
