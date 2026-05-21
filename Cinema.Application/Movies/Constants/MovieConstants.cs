namespace Cinema.Application.Movies.Constants;

/// <summary>
/// Domain-level constants for the Movies feature.
/// Centralises all magic numbers and strings that appear in handlers, queries, and validators.
/// </summary>
public static class MovieConstants
{
    /// <summary>Maximum number of cast members to import per movie from TMDB.</summary>
    public const int MaxImportedCastMembers = 12;

    /// <summary>Default page size for the movie listing query.</summary>
    public const int DefaultPageSize = 10;

    /// <summary>Default number of personalised recommendations returned.</summary>
    public const int DefaultRecommendationCount = 5;

    /// <summary>Maximum allowed length for a movie title.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Maximum allowed length for a movie description / overview.</summary>
    public const int MaxDescriptionLength = 2000;

    /// <summary>
    /// Earliest valid release year (1888 — Roundhay Garden Scene, the oldest surviving film).
    /// Validators should use <c>GreaterThanOrEqualTo(EarliestReleaseYear)</c>.
    /// </summary>
    public const int EarliestReleaseYear = 1888;

    /// <summary>
    /// Country codes checked in priority order when extracting an age-rating certification.
    /// First non-empty certification found wins.
    /// </summary>
    public static readonly string[] AgeRatingCountryPriority = ["UA", "US"];
}
