using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class MovieGenre
{
    public EntityId<Movie> MovieId { get; private set; }
    public Movie? Movie { get; private set; }

    public EntityId<Genre> GenreId { get; private set; }
    public Genre? Genre { get; private set; }

    private MovieGenre(EntityId<Movie> movieId, EntityId<Genre> genreId)
    {
        MovieId = movieId;
        GenreId = genreId;
    }

    private MovieGenre(Movie movie, Genre genre)
    {
        MovieId = movie.Id;
        Movie = movie;
        GenreId = genre.Id;
        Genre = genre;
    }

    public static MovieGenre New(EntityId<Movie> movieId, EntityId<Genre> genreId)
        => new(movieId, genreId);

    public static MovieGenre Create(Movie movie, Genre genre)
        => new(movie, genre);
}