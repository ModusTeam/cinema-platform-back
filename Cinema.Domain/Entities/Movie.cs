using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class Movie
{
    public EntityId<Movie> Id { get; }
    public string Title { get; private set; }
    public int ExternalId { get; } // TMDB id
    public int DurationMinutes { get; private set; }
    public decimal Rating { get; private set; }
    public string? ImgUrl { get; set; }
    public string? VideoUrl { get; set; }

    public ICollection<MovieGenre>? MovieGenres { get; private set; } = [];
    public ICollection<Session>? Sessions { get; private set; } = [];

    private Movie(
        EntityId<Movie> id,
        int externalId,
        string title,
        int durationMinutes,
        decimal rating,
        string? imgUrl,
        string? videoUrl)
    {
        Id = id;
        ExternalId = externalId;
        Title = title;
        DurationMinutes = durationMinutes;
        Rating = rating;
        ImgUrl = imgUrl;
        VideoUrl = videoUrl;
    }

    public static Movie New(
        EntityId<Movie> id,
        int externalId,
        string title,
        int durationMinutes,
        decimal rating,
        string? imgUrl,
        string? videoUrl)
        => new(id, externalId, title, durationMinutes, rating, imgUrl, videoUrl);
}