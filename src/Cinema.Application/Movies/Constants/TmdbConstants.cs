namespace Cinema.Application.Movies.Constants;

/// <summary>
/// Constants specific to the TMDB external API integration used within the Application layer.
/// </summary>
public static class TmdbConstants
{
    // Video filtering
    public const string VideoSiteYouTube = "YouTube";
    public const string VideoTypeTrailer = "Trailer";
    public const string VideoTypeTeaser = "Teaser";
    public const string VideoTypeClip = "Clip";
    public const string VideoTypeFeaturette = "Featurette";
    public const string VideoTypeBehindTheScenes = "Behind the Scenes";
    public const string VideoTypeBloopers = "Bloopers";

    /// <summary>Base URL used to build a YouTube watch link from a video key.</summary>
    public const string YouTubeWatchBaseUrl = "https://www.youtube.com/watch?v=";

    // In-process memory cache (SearchTmdbQuery)
    /// <summary>Prefix for TMDB search result cache keys.</summary>
    public const string SearchCacheKeyPrefix = "tmdb_search_";

    /// <summary>How long TMDB search results are kept in the local memory cache.</summary>
    public static readonly TimeSpan SearchCacheDuration = TimeSpan.FromMinutes(5);
}