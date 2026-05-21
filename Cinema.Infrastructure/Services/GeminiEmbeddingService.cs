using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Gemini;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Options;
using Cinema.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;
using Cinema.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Infrastructure.Services;

public class GeminiEmbeddingService(
    IGeminiApi geminiApi,
    IServiceScopeFactory scopeFactory,
    IOptions<GeminiOptions> options,
    ILogger<GeminiEmbeddingService> logger) : IAiEmbeddingService
{
    private readonly GeminiOptions _settings = options.Value;

    public async Task<Result<float[]>> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result.Failure<float[]>(new Error("Gemini.Validation", "Text cannot be empty"));

        try
        {
            var request = new GeminiRequest
            {
                Content = new GeminiContent
                {
                    Parts = [new GeminiPart { Text = text }]
                }
            };
            
            var response = await geminiApi.GenerateEmbeddingAsync(_settings.EmbeddingModelId, _settings.ApiKey, request);

            if (response?.Embedding?.Values == null || response.Embedding.Values.Length == 0)
            {
                logger.LogWarning("Gemini API returned success but no embedding data.");
                return Result.Failure<float[]>(new Error("Gemini.EmptyResponse", "API returned empty embedding"));
            }

            return Result.Success(response.Embedding.Values);
        }
        catch (ApiException ex)
        {
            logger.LogError(ex, "Gemini API Error. Status: {Status}, Content: {Content}", ex.StatusCode, ex.Content);
            return Result.Failure<float[]>(new Error("Gemini.ApiError", $"API Error: {ex.StatusCode}"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during Gemini embedding generation");
            return Result.Failure<float[]>(new Error("Gemini.Exception", "An error occurred while calling AI service"));
        }
    }

    public async Task UpdateMovieEmbeddingAsync(Guid movieId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var movie = await context.Movies
            .Include(m => m.MovieGenres)
                .ThenInclude(mg => mg.Genre)
            .FirstOrDefaultAsync(m => m.Id == new EntityId<Movie>(movieId), ct);
        if (movie == null)
        {
            logger.LogWarning("Movie with ID {MovieId} not found for embedding update.", movieId);
            return;
        }
        
        var textToEmbed = $"{movie.Title}. {movie.Description}. Genres: {string.Join(", ", movie.MovieGenres.Where(mg => mg.Genre != null).Select(mg => mg.Genre!.Name))}";

        var embeddingResult = await GenerateEmbeddingAsync(textToEmbed, ct);

        if (embeddingResult.IsFailure)
        {
            logger.LogError("Failed to update embedding for movie {Title}. Error: {Error}", movie.Title, embeddingResult.Error.Description);
            return;
        }

        movie.SetEmbedding(embeddingResult.Value);
        await context.SaveChangesAsync(ct);
        
        logger.LogInformation("Successfully updated embedding for movie {Title}", movie.Title);
    }
}