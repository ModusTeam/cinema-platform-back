using Cinema.Domain.Shared;

namespace Cinema.Domain.Errors;

/// <summary>
/// Strongly-typed errors for failures that originate from the TMDB external API.
/// </summary>
public static class TmdbErrors
{
    public static readonly Error NotFound =
        new("Tmdb.NotFound", "The requested movie was not found in TMDB.");

    public static readonly Error FetchFailed =
        new("Tmdb.Error", "Failed to retrieve movie data from TMDB.");
}
