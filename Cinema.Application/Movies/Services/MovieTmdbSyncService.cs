using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Tmdb;
using Cinema.Application.Common.Settings;
using Cinema.Application.Movies.Constants;
using Cinema.Domain.Entities;
using Cinema.Domain.Errors;
using Cinema.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;

namespace Cinema.Application.Movies.Services;

public class MovieTmdbSyncService(
    IApplicationDbContext context,
    ITmdbApi tmdbApi,
    IOptions<TmdbSettings> settings,
    ILogger<MovieTmdbSyncService> logger) : IMovieTmdbSyncService
{
    private const string PrimaryTmdbLanguage = "uk-UA";
    private const string FallbackTmdbLanguage = "en-US";
    private const string DetailsAppendToResponse = "credits,videos,release_dates";
    private readonly TmdbSettings _settings = settings.Value;

    public async Task<Result> ApplyLatestTmdbDetailsAsync(
        Movie movie,
        string? ageRestrictionOverride = null,
        CancellationToken ct = default)
    {
        if (!movie.ExternalId.HasValue)
            return Result.Failure(MovieErrors.NotImportedFromTmdb);

        var detailsResult = await FetchTmdbDetailsAsync(movie.ExternalId.Value, ct);
        if (detailsResult.IsFailure)
            return Result.Failure(detailsResult.Error);

        var details = detailsResult.Value;
        var posterUrl = !string.IsNullOrWhiteSpace(details.PosterPath)
            ? $"{_settings.ImageBaseUrl}{details.PosterPath}"
            : null;
        var backdropUrl = !string.IsNullOrWhiteSpace(details.BackdropPath)
            ? $"{_settings.ImageBaseUrl}{details.BackdropPath}"
            : null;
        var ageRestriction = !string.IsNullOrWhiteSpace(ageRestrictionOverride)
            ? ageRestrictionOverride
            : ExtractAgeRestriction(details);

        movie.UpdateFromTmdb(
            details.Title,
            details.Overview,
            details.Runtime ?? 0,
            (decimal)details.VoteAverage,
            DateTime.TryParse(details.ReleaseDate, out var releaseDate) ? releaseDate : null,
            posterUrl,
            backdropUrl,
            ExtractTrailerUrl(details),
            ageRestriction);

        movie.ClearGenres();
        await SyncGenresAsync(movie, details.Genres, ct);

        if (details.Credits?.Cast != null)
        {
            var cast = details.Credits.Cast
                .OrderBy(c => c.Order)
                .Take(MovieConstants.MaxImportedCastMembers)
                .Select(c => new MovieCastMember
                {
                    ExternalId = c.Id,
                    Name = c.Name,
                    Role = c.Character,
                    PhotoUrl = !string.IsNullOrWhiteSpace(c.ProfilePath)
                        ? $"{_settings.ImageBaseUrl}{c.ProfilePath}"
                        : null
                });

            movie.SetCast(cast);
        }

        return Result.Success();
    }

    private async Task<Result<TmdbMovieDetails>> FetchTmdbDetailsAsync(int tmdbId, CancellationToken ct)
    {
        try
        {
            var details = await tmdbApi.GetMovieDetailsAsync(
                tmdbId,
                _settings.ApiKey,
                PrimaryTmdbLanguage,
                DetailsAppendToResponse,
                ct);

            if (ShouldFetchFallbackDetails(details))
            {
                details = await MergeFallbackDetailsAsync(details, tmdbId, ct);
            }

            return Result.Success(details);
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Result.Failure<TmdbMovieDetails>(TmdbErrors.NotFound);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch TMDB details for movie {TmdbId}", tmdbId);
            return Result.Failure<TmdbMovieDetails>(TmdbErrors.FetchFailed);
        }
    }

    private bool ShouldFetchFallbackDetails(TmdbMovieDetails details)
    {
        return string.IsNullOrWhiteSpace(details.Overview) || ExtractTrailerUrl(details) is null;
    }

    private async Task<TmdbMovieDetails> MergeFallbackDetailsAsync(
        TmdbMovieDetails primary,
        int tmdbId,
        CancellationToken ct)
    {
        try
        {
            var fallback = await tmdbApi.GetMovieDetailsAsync(
                tmdbId,
                _settings.ApiKey,
                FallbackTmdbLanguage,
                DetailsAppendToResponse,
                ct);

            if (string.IsNullOrWhiteSpace(primary.Overview) && !string.IsNullOrWhiteSpace(fallback.Overview))
            {
                primary.Overview = fallback.Overview;
            }

            if (ExtractTrailerUrl(primary) is null && ExtractTrailerUrl(fallback) is not null)
            {
                primary.Videos = fallback.Videos;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to fetch TMDB fallback details for movie {TmdbId}. Sync will continue with primary language details.",
                tmdbId);
        }

        return primary;
    }

    private string? ExtractTrailerUrl(TmdbMovieDetails details)
    {
        var videos = details.Videos?.Results?
            .Where(v =>
                string.Equals(v.Site, TmdbConstants.VideoSiteYouTube, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(v.Key))
            .ToList();

        if (videos is not { Count: > 0 })
            return null;

        var trailer = videos.FirstOrDefault(v =>
                string.Equals(v.Type, TmdbConstants.VideoTypeTrailer, StringComparison.OrdinalIgnoreCase))
            ?? videos.FirstOrDefault(v =>
                string.Equals(v.Type, TmdbConstants.VideoTypeTeaser, StringComparison.OrdinalIgnoreCase));

        return trailer != null
            ? $"{TmdbConstants.YouTubeWatchBaseUrl}{trailer.Key}"
            : null;
    }

    private async Task SyncGenresAsync(Movie movie, List<TmdbGenreDto> tmdbGenres, CancellationToken ct)
    {
        if (tmdbGenres is not { Count: > 0 }) return;

        var tmdbGenreIds = tmdbGenres.Select(g => g.Id).ToList();
        var tmdbGenreNames = tmdbGenres.Select(g => g.Name.ToLower()).ToList();

        var existingGenres = await context.Genres
            .Where(g => (g.ExternalId != null && tmdbGenreIds.Contains(g.ExternalId.Value))
                        || tmdbGenreNames.Contains(g.Name.ToLower()))
            .ToListAsync(ct);

        foreach (var tmdbGenre in tmdbGenres)
        {
            var genre = existingGenres.FirstOrDefault(g => g.ExternalId == tmdbGenre.Id)
                        ?? existingGenres.FirstOrDefault(g => string.Equals(g.Name, tmdbGenre.Name, StringComparison.OrdinalIgnoreCase));

            if (genre is null)
            {
                genre = Genre.Import(tmdbGenre.Id, tmdbGenre.Name);
                context.Genres.Add(genre);
                existingGenres.Add(genre);
            }

            movie.AddGenre(genre);
        }
    }

    private static string? ExtractAgeRestriction(TmdbMovieDetails details)
    {
        var results = details.ReleaseDates?.Results;
        if (results is not { Count: > 0 }) return null;

        string? rawCertification = null;

        foreach (string countryCode in MovieConstants.AgeRatingCountryPriority)
        {
            var countryEntry = results.FirstOrDefault(r => r.CountryCode == countryCode);
            string? cert = countryEntry?.ReleaseDates
                .Select(rd => rd.Certification)
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

            if (!string.IsNullOrWhiteSpace(cert))
            {
                rawCertification = cert;
                break;
            }
        }

        return rawCertification switch
        {
            "G" => "0+",
            "PG" => "7+",
            "PG-13" => "12+",
            "R" => "16+",
            "NC-17" => "18+",
            "18+" => "18+",
            "16+" => "16+",
            "12+" => "12+",
            "0+" => "0+",
            _ => rawCertification
        };
    }
}
