using Cinema.Domain.Common;
using Cinema.Domain.Enums;
using Cinema.Domain.Events;
using Cinema.Domain.Exceptions;
using Pgvector;

namespace Cinema.Domain.Entities;

public class MovieCastMember
{
    public int ExternalId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string? PhotoUrl { get; set; }
}

public class Movie : BaseEntity
{
    public EntityId<Movie> Id { get; private set; }
    public int? ExternalId { get; private set; }
    public string Title { get; private set; }
    public string? Description { get; private set; }
    public int DurationMinutes { get; private set; }
    public decimal Rating { get; private set; }
    public int ReleaseYear { get; private set; }
    
    public string? PosterUrl { get; private set; }
    public string? BackdropUrl { get; private set; }
    public string? TrailerUrl { get; private set; }
    
    public MovieStatus Status { get; private set; } = MovieStatus.ComingSoon;
    public bool IsDeleted { get; private set; } = false;
    
    public List<MovieCastMember> Cast { get; set; } = new();

    public ICollection<MovieGenre> MovieGenres { get; private set; } = [];
    public ICollection<Session> Sessions { get; private set; } = [];
    public Vector? Embedding { get; private set; }
    
    private Movie() { }

    public static Movie CreateManual(
        string title,
        string description,
        int durationMinutes,
        int releaseYear,
        MovieStatus status)
    {
        var movie = new Movie
        {
            Id = new EntityId<Movie>(Guid.NewGuid()),
            Title = title,
            Description = description,
            DurationMinutes = durationMinutes,
            ReleaseYear = releaseYear,
            Status = status,
            ExternalId = null,
            Rating = 0
        };

        return movie;
    }
    
    public void ChangeStatus(MovieStatus newStatus)
    {
        Status = newStatus;
    }
    
    public void Delete()
    {
        IsDeleted = true;
    }
    
    private Movie(
        EntityId<Movie> id,
        int? externalId,
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
        var movie = new Movie(
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

        return movie;
    }
    
    public void AddGenre(Genre genre)
    {
        if (!MovieGenres.Any(x => x.GenreId == genre.Id))
        {
            MovieGenres.Add(MovieGenre.New(this.Id, genre.Id));
        }
    }

    public void Rename(string newTitle)
    {
        if (string.IsNullOrWhiteSpace(newTitle)) throw new DomainException("Title cannot be empty.");
        Title = newTitle;
    }

    public void UpdateImages(string? posterUrl, string? backdropUrl, string? trailerUrl)
    {
        PosterUrl = posterUrl;
        BackdropUrl = backdropUrl;
        TrailerUrl = trailerUrl;
    }
    
    public void UpdateSpecs(string? description, int? durationMinutes, decimal? rating, int? releaseYear)
    {
        if (description is not null) Description = description;
        if (durationMinutes.HasValue) DurationMinutes = durationMinutes.Value;
        if (rating.HasValue) Rating = rating.Value;
        if (releaseYear.HasValue) ReleaseYear = releaseYear.Value;
    }
    
    public void SetEmbedding(float[] embedding)
    {
        Embedding = new Vector(embedding);
    }
}