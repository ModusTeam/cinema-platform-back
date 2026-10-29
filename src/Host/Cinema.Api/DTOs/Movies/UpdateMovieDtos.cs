using System.Text.Json.Serialization;
using Cinema.Catalog.Application.Common.Models.MovieImages;
using Cinema.Catalog.Application.Movies.Models;

namespace Cinema.Api.DTOs.Movies;

public record RenameMovieDto(string Title);

public sealed class UpdateMovieImagesDto
{
    private string? _posterUrl;
    public string? PosterUrl
    {
        get => _posterUrl;
        init { _posterUrl = value; HasPosterUrl = true; }
    }
    [JsonIgnore] public bool HasPosterUrl { get; private set; }

    private string? _backdropUrl;
    public string? BackdropUrl
    {
        get => _backdropUrl;
        init { _backdropUrl = value; HasBackdropUrl = true; }
    }
    [JsonIgnore] public bool HasBackdropUrl { get; private set; }

    private string? _trailerUrl;
    public string? TrailerUrl
    {
        get => _trailerUrl;
        init { _trailerUrl = value; HasTrailerUrl = true; }
    }
    [JsonIgnore] public bool HasTrailerUrl { get; private set; }

    private MovieImageReference? _poster;
    public MovieImageReference? Poster
    {
        get => _poster;
        init { _poster = value; HasPoster = true; }
    }
    [JsonIgnore] public bool HasPoster { get; private set; }

    private MovieImageReference? _backdrop;
    public MovieImageReference? Backdrop
    {
        get => _backdrop;
        init { _backdrop = value; HasBackdrop = true; }
    }
    [JsonIgnore] public bool HasBackdrop { get; private set; }

}

public sealed class UpdateMovieDetailsDto
{
    public string? Description { get; init; }
    public int? DurationMinutes { get; init; }
    public double? Rating { get; init; }
    public int? ReleaseYear { get; init; }
    public IReadOnlyList<Guid>? GenreIds { get; init; }
    public IReadOnlyList<ManualCastMember>? Cast { get; init; }
    private string? _ageRestriction;
    public string? AgeRestriction
    {
        get => _ageRestriction;
        init { _ageRestriction = value; HasAgeRestriction = true; }
    }
    [JsonIgnore] public bool HasAgeRestriction { get; private set; }

}
