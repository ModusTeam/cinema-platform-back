using Cinema.Application.Common.Models.Tmdb;
using Cinema.Application.Movies.Constants;
using Cinema.Domain.Errors;
using Cinema.Domain.Shared;

namespace Cinema.Application.Movies.Helpers;

public static class TmdbMovieDetailsHelper
{
    private const int MissingVideoRank = int.MaxValue;

    public static bool ShouldFetchFallbackDetails(TmdbMovieDetails details)
    {
        return NormalizeOverview(details.Overview) is null ||
               GetBestYouTubeVideoRank(details) > GetVideoTypeRank(TmdbConstants.VideoTypeTrailer);
    }

    public static void MergeFallbackDetails(TmdbMovieDetails primary, TmdbMovieDetails fallback)
    {
        primary.Overview = NormalizeOverview(primary.Overview) ?? NormalizeOverview(fallback.Overview);

        if (GetBestYouTubeVideoRank(fallback) < GetBestYouTubeVideoRank(primary))
        {
            primary.Videos = fallback.Videos;
        }
    }

    public static void NormalizeForPersistence(TmdbMovieDetails details)
    {
        details.Overview = NormalizeOverview(details.Overview);
    }

    public static Result<int> ResolveRuntime(TmdbMovieDetails details, int? fallbackRuntime = null)
    {
        if (details.Runtime is > 0)
        {
            return Result.Success(details.Runtime.Value);
        }

        if (fallbackRuntime is > 0)
        {
            return Result.Success(fallbackRuntime.Value);
        }

        return Result.Failure<int>(TmdbErrors.MissingRuntime);
    }

    public static string? ExtractTrailerUrl(TmdbMovieDetails details)
    {
        var video = GetBestYouTubeVideo(details);
        var key = video?.Key?.Trim();

        return string.IsNullOrWhiteSpace(key)
            ? null
            : $"{TmdbConstants.YouTubeWatchBaseUrl}{key}";
    }

    private static string? NormalizeOverview(string? overview)
    {
        if (string.IsNullOrWhiteSpace(overview))
        {
            return null;
        }

        var normalized = overview.Trim();

        return normalized.Length <= MovieConstants.MaxDescriptionLength
            ? normalized
            : normalized[..MovieConstants.MaxDescriptionLength];
    }

    private static TmdbVideoDto? GetBestYouTubeVideo(TmdbMovieDetails details)
    {
        return details.Videos?.Results?
            .Where(IsUsableYouTubeVideo)
            .OrderBy(GetVideoRank)
            .FirstOrDefault();
    }

    private static int GetBestYouTubeVideoRank(TmdbMovieDetails details)
    {
        var video = GetBestYouTubeVideo(details);

        return video is null
            ? MissingVideoRank
            : GetVideoRank(video);
    }

    private static bool IsUsableYouTubeVideo(TmdbVideoDto video)
    {
        return string.Equals(video.Site, TmdbConstants.VideoSiteYouTube, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(video.Key);
    }

    private static int GetVideoRank(TmdbVideoDto video)
    {
        return GetVideoTypeRank(video.Type);
    }

    private static int GetVideoTypeRank(string? videoType)
    {
        if (string.Equals(videoType, TmdbConstants.VideoTypeTrailer, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(videoType, TmdbConstants.VideoTypeTeaser, StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(videoType, TmdbConstants.VideoTypeClip, StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(videoType, TmdbConstants.VideoTypeFeaturette, StringComparison.OrdinalIgnoreCase)) return 3;
        if (string.Equals(videoType, TmdbConstants.VideoTypeBehindTheScenes, StringComparison.OrdinalIgnoreCase)) return 4;
        if (string.Equals(videoType, TmdbConstants.VideoTypeBloopers, StringComparison.OrdinalIgnoreCase)) return 5;

        return 100;
    }
}