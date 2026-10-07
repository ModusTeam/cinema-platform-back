using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Tmdb;
using Cinema.Application.Common.Settings;
using Cinema.Application.Movies.Constants;
using Cinema.Application.Movies.Helpers;
using Cinema.Domain.Entities;
using Cinema.Domain.Errors;
using Cinema.Domain.Shared;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data;

namespace Cinema.Application.Movies.Commands.ImportMovie;

public class ImportMovieCommandHandler(
    IApplicationDbContext context,
    ITmdbApi tmdbApi,
    IOptions<TmdbSettings> settings,
    IBackgroundJobClient jobClient,
    ILogger<ImportMovieCommandHandler> logger
    ) : IRequestHandler<ImportMovieCommand, Result<Guid>>
{
    private const string PrimaryTmdbLanguage = "uk-UA";
    private const string FallbackTmdbLanguage = "en-US";
    private const string DetailsAppendToResponse = "credits,videos,release_dates";
    private readonly TmdbSettings _settings = settings.Value;

    public async Task<Result<Guid>> Handle(ImportMovieCommand request, CancellationToken ct)
    {
        var existing = await context.Movies
            .Include(m => m.MovieGenres)
            .FirstOrDefaultAsync(m => m.ExternalId == request.TmdbId, ct);

        if (existing is not null && !existing.IsDeleted)
        {
            logger.LogWarning("Movie with TMDB ID {TmdbId} is already imported and active.", request.TmdbId);
            return Result.Failure<Guid>(MovieErrors.AlreadyImported);
        }

        var detailsResult = await FetchTmdbDetailsAsync(request.TmdbId, ct);
        if (detailsResult.IsFailure)
        {
            logger.LogError("Failed to fetch TMDB details for movie {TmdbId}", request.TmdbId);
            return Result.Failure<Guid>(detailsResult.Error);
        }

        var details = detailsResult.Value;
        var runtimeResult = TmdbMovieDetailsHelper.ResolveRuntime(details, existing?.DurationMinutes);
        if (runtimeResult.IsFailure)
        {
            return Result.Failure<Guid>(runtimeResult.Error);
        }

        var dbContext = (DbContext)context;
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            try
            {
                var movie = existing;
                var isRestored = false;

                if (movie is not null && movie.IsDeleted)
                {
                    movie.Restore();
                    movie.UpdateFromTmdb(
                        details.Title,
                        details.Overview,
                        runtimeResult.Value,
                        (decimal)details.VoteAverage,
                        DateTime.TryParse(details.ReleaseDate, out var d) ? d : null,
                        !string.IsNullOrEmpty(details.PosterPath) ? $"{_settings.ImageBaseUrl}{details.PosterPath}" : null,
                        !string.IsNullOrEmpty(details.BackdropPath) ? $"{_settings.ImageBaseUrl}{details.BackdropPath}" : null,
                        TmdbMovieDetailsHelper.ExtractTrailerUrl(details)
                    );
                    movie.ClearGenres();
                    isRestored = true;
                }
                else
                {
                    movie = MapToMovie(details, runtimeResult.Value);
                    context.Movies.Add(movie);
                }

                var ageRestriction = !string.IsNullOrWhiteSpace(request.AgeRestrictionOverride)
                    ? request.AgeRestrictionOverride
                    : ExtractAgeRestriction(details);
                movie.SetAgeRestriction(ageRestriction);

                if (isRestored && details.Credits?.Cast != null)
                {
                    var cast = details.Credits.Cast
                        .OrderBy(c => c.Order)
                        .Take(MovieConstants.MaxImportedCastMembers)
                        .Select(c => new MovieCastMember
                        {
                            ExternalId = c.Id,
                            Name       = c.Name,
                            Role       = c.Character,
                            PhotoUrl   = !string.IsNullOrEmpty(c.ProfilePath)
                                ? $"{_settings.ImageBaseUrl}{c.ProfilePath}" : null
                        });
                    movie.SetCast(cast);
                }

                await SyncGenresAsync(movie, details.Genres, ct);
                await context.SaveChangesAsync(ct);

                await transaction.CommitAsync(ct);

                if (isRestored)
                {
                    logger.LogInformation("Restored existing soft-deleted movie {TmdbId}", request.TmdbId);
                }

                // Hangfire does not support cancellation tokens on background jobs.
                jobClient.Enqueue<IAiEmbeddingService>(s =>
                    s.UpdateMovieEmbeddingAsync(movie.Id.Value, CancellationToken.None));

                return Result.Success(movie.Id.Value);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to import movie {TmdbId}", request.TmdbId);
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
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

            if (TmdbMovieDetailsHelper.ShouldFetchFallbackDetails(details))
            {
                details = await MergeFallbackDetailsAsync(details, tmdbId, ct);
            }

            TmdbMovieDetailsHelper.NormalizeForPersistence(details);
            return Result.Success(details);
        }
        catch (Refit.ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Result.Failure<TmdbMovieDetails>(TmdbErrors.NotFound);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch TMDB details for movie {TmdbId}", tmdbId);
            return Result.Failure<TmdbMovieDetails>(TmdbErrors.FetchFailed);
        }
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

            TmdbMovieDetailsHelper.MergeFallbackDetails(primary, fallback);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to fetch TMDB fallback details for movie {TmdbId}. Import will continue with primary language details.",
                tmdbId);
        }

        return primary;
    }

    private Movie MapToMovie(TmdbMovieDetails details, int runtimeMinutes)
    {
        var posterUrl = !string.IsNullOrEmpty(details.PosterPath)
            ? $"{_settings.ImageBaseUrl}{details.PosterPath}" : null;

        var backdropUrl = !string.IsNullOrEmpty(details.BackdropPath)
            ? $"{_settings.ImageBaseUrl}{details.BackdropPath}" : null;

        var trailerUrl = TmdbMovieDetailsHelper.ExtractTrailerUrl(details);

        var movie = Movie.Import(
            details.Id,
            details.Title,
            details.Overview,
            runtimeMinutes,
            (decimal)details.VoteAverage,
            DateTime.TryParse(details.ReleaseDate, out var d) ? d : null,
            posterUrl,
            backdropUrl,
            trailerUrl
        );

        if (details.Credits?.Cast != null)
        {
            var cast = details.Credits.Cast
                .OrderBy(c => c.Order)
                .Take(MovieConstants.MaxImportedCastMembers)
                .Select(c => new MovieCastMember
                {
                    ExternalId = c.Id,
                    Name       = c.Name,
                    Role       = c.Character,
                    PhotoUrl   = !string.IsNullOrEmpty(c.ProfilePath)
                        ? $"{_settings.ImageBaseUrl}{c.ProfilePath}" : null
                });

            movie.SetCast(cast);
        }

        return movie;
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
            "G"     => "0+",
            "PG"    => "7+",
            "PG-13" => "12+",
            "R"     => "16+",
            "NC-17" => "18+",
            "18+"   => "18+",
            "16+"   => "16+",
            "12+"   => "12+",
            "0+"    => "0+",
            _       => rawCertification
        };
    }
}