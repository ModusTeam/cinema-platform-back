namespace Cinema.Api.DTOs.Movies;

public record MovieRecommendationDto(
    Guid Id,
    string Title,
    string? PosterUrl,
    double SimilarityScore
);
