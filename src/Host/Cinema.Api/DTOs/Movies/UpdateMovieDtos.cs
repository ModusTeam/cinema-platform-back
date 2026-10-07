namespace Cinema.Api.DTOs.Movies;

public record RenameMovieDto(string Title);

public record UpdateMovieImagesDto(
    string? PosterUrl,
    string? BackdropUrl,
    string? TrailerUrl
);

public record UpdateMovieDetailsDto(
    string? Description,
    int? DurationMinutes,
    double? Rating,
    int? ReleaseYear
);
