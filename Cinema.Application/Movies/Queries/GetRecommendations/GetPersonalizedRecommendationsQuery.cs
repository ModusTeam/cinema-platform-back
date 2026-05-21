using Cinema.Application.Common.Interfaces;
using Cinema.Application.Movies.Constants;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Cinema.Application.Movies.Queries.GetRecommendations;

public record MovieRecommendationResult(Guid Id, string Title, string? PosterUrl, double SimilarityScore);

public record GetPersonalizedRecommendationsQuery(int Count = MovieConstants.DefaultRecommendationCount) : IRequest<Result<List<MovieRecommendationResult>>>;

public class GetPersonalizedRecommendationsQueryHandler(
    IApplicationDbContext context,
    ICurrentUserService currentUserService)
    : IRequestHandler<GetPersonalizedRecommendationsQuery, Result<List<MovieRecommendationResult>>>
{
    public async Task<Result<List<MovieRecommendationResult>>> Handle(GetPersonalizedRecommendationsQuery request, CancellationToken ct)
    {
        var userId = currentUserService.UserId;
        if (userId == null) return Result.Failure<List<MovieRecommendationResult>>(new Error("Auth.Required", "User not found"));
        
        var userHistoryVectors = await context.Orders
            .AsNoTracking()
            .Where(o => o.UserId == userId && o.Status == Domain.Enums.OrderStatus.Paid)
            .SelectMany(o => o.Tickets)
            .Select(t => t.Session!.Movie!)
            .Where(m => m.Embedding != null)
            .Select(m => m.Embedding!)
            .ToListAsync(ct);

        if (!userHistoryVectors.Any())
        {
            return Result.Success(new List<MovieRecommendationResult>());
        }
        
        var vectorSize = userHistoryVectors.First()!.ToString()!.Split(',').Length;

        var vectors = userHistoryVectors.Select(v => v!.ToArray()).ToList();
        var avgVector = new float[vectors[0].Length];

        foreach (var vec in vectors)
        {
            for (int i = 0; i < vec.Length; i++)
            {
                avgVector[i] += vec[i];
            }
        }

        for (int i = 0; i < avgVector.Length; i++)
        {
            avgVector[i] /= vectors.Count;
        }
        
        var userPreferenceVector = new Vector(avgVector);

        var watchedMovieIds = await context.Orders
            .Where(o => o.UserId == userId)
            .SelectMany(o => o.Tickets)
            .Select(t => t.Session!.MovieId)
            .Distinct()
            .ToListAsync(ct);

        var recommendations = await context.Movies
            .AsNoTracking()
            .Where(m => !watchedMovieIds.Contains(m.Id) && m.Embedding != null)
            .OrderBy(m => m.Embedding!.CosineDistance(userPreferenceVector))
            .Take(request.Count)
            .Select(m => new MovieRecommendationResult(
                m.Id.Value,
                m.Title,
                m.PosterUrl,
                1 - m.Embedding!.CosineDistance(userPreferenceVector) // Score (1 - distance = similarity)
            ))
            .ToListAsync(ct);

        return Result.Success(recommendations);
    }
}
