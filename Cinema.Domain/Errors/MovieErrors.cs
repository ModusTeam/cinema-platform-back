using Cinema.Domain.Shared;

namespace Cinema.Domain.Errors;

/// <summary>
/// Centralised, strongly-typed errors for the Movie domain.
/// Use these instead of inline <c>new Error("...", "...")</c> literals.
/// </summary>
public static class MovieErrors
{
    public static readonly Error NotFound =
        new("Movie.NotFound", "A movie with the specified identifier was not found.");

    public static readonly Error AlreadyImported =
        new("Movie.Exists", "This movie has already been imported from TMDB.");
}
