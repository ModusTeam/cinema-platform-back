using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class MovieCastMember
{
    public int ExternalId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string? PhotoUrl { get; set; }
}

public class Movie
{
    public EntityId<Movie> Id { get; }
    public int ExternalId { get; private set; }
    public string Title { get; private set; }
    public string? Description { get; private set; }
    public int DurationMinutes { get; private set; }
    public decimal Rating { get; private set; }
    public int ReleaseYear { get; private set; }
    
    public string? PosterUrl { get; private set; }
    public string? BackdropUrl { get; private set; }
    public string? TrailerUrl { get; private set; }

    public List<MovieCastMember> Cast { get; set; } = new();

    public ICollection<MovieGenre> MovieGenres { get; private set; } = [];
    public ICollection<Session> Sessions { get; private set; } = [];

    private Movie() { }

    private Movie(
        EntityId<Movie> id,
        int externalId,
        string title,
        string? description,
        int durationMinutes,
        decimal rating,
        int releaseYear,
        string? posterUrl,
        string? backdropUrl,
        string? trailerUrl)
    {
        Id = id;
        ExternalId = externalId;
        Title = title;
        Description = description;
        DurationMinutes = durationMinutes;
        Rating = rating;
        ReleaseYear = releaseYear;
        PosterUrl = posterUrl;
        BackdropUrl = backdropUrl;
        TrailerUrl = trailerUrl;
    }

    public static Movie Import(
        int externalId, 
        string title, 
        string? description, 
        int duration, 
        decimal rating, 
        DateTime? releaseDate, 
        string? posterUrl, 
        string? backdropUrl,
        string? trailerUrl)
    {
        return new Movie(
            EntityId<Movie>.New(),
            externalId,
            title,
            description,
            duration,
            rating,
            releaseDate?.Year ?? DateTime.UtcNow.Year,
            posterUrl,
            backdropUrl,
            trailerUrl
        );
    }
    
    public void AddGenre(Genre genre)
    {
        if (!MovieGenres.Any(x => x.GenreId == genre.Id))
        {
            MovieGenres.Add(MovieGenre.New(this.Id, genre.Id));
        }
    }

    public void UpdateDetails(string title, string? description, string? posterUrl, string? backdropUrl, string? trailerUrl)
    {
        Title = title;
        Description = description;
        PosterUrl = posterUrl;
        BackdropUrl = backdropUrl;
        TrailerUrl = trailerUrl;
    }
}