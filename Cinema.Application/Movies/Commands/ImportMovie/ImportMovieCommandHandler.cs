using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Tmdb;
using Cinema.Application.Common.Settings;
using Cinema.Application.Movies.Constants;
using Cinema.Domain.Entities;
using Cinema.Domain.Errors;
using Cinema.Domain.Shared;
using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Data;

namespace Cinema.Application.Movies.Commands.ImportMovie;

public class ImportMovieCommandHandler(
    IApplicationDbContext context,
    ITmdbApi tmdbApi,
    IOptions<TmdbSettings> settings,
    IBackgroundJobClient jobClient
    ) : IRequestHandler<ImportMovieCommand, Result<Guid>>
{
    private readonly TmdbSettings _settings = settings.Value;

    public async Task<Result<Guid>> Handle(ImportMovieCommand request, CancellationToken ct)
    {
        if (await context.Movies.AnyAsync(m => m.ExternalId == request.TmdbId, ct))
            return Result.Failure<Guid>(MovieErrors.AlreadyImported);

        var detailsResult = await FetchTmdbDetailsAsync(request.TmdbId, ct);
        if (detailsResult.IsFailure)
            return Result.Failure<Guid>(detailsResult.Error);

        var details = detailsResult.Value;
        var dbContext = (DbContext)context;
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            try
            {
                var movie = MapToMovie(details);
                movie.SetAgeRestriction(ExtractAgeRestriction(details));

                await SyncGenresAsync(movie, details.Genres, ct);
                context.Movies.Add(movie);
                await context.SaveChangesAsync(ct);

                await transaction.CommitAsync(ct);

                // Hangfire does not support cancellation tokens on background jobs —
                // CancellationToken.None is correct and intentional here.
                jobClient.Enqueue<IAiEmbeddingService>(s =>
                    s.UpdateMovieEmbeddingAsync(movie.Id.Value, CancellationToken.None));

                return Result.Success(movie.Id.Value);
            }
            catch (Exception)
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<Result<TmdbMovieDetails>> FetchTmdbDetailsAsync(int tmdbId, CancellationToken ct)
    {
        try
        {
            var details = await tmdbApi.GetMovieDetailsAsync(tmdbId, _settings.ApiKey, ct);
            return Result.Success(details);
        }
        catch (Refit.ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Result.Failure<TmdbMovieDetails>(TmdbErrors.NotFound);
        }
        catch (Exception)
        {
            return Result.Failure<TmdbMovieDetails>(TmdbErrors.FetchFailed);
        }
    }

    private Movie MapToMovie(TmdbMovieDetails details)
    {
        var posterUrl = !string.IsNullOrEmpty(details.PosterPath)
            ? $"{_settings.ImageBaseUrl}{details.PosterPath}" : null;

        var backdropUrl = !string.IsNullOrEmpty(details.BackdropPath)
            ? $"{_settings.ImageBaseUrl}{details.BackdropPath}" : null;

        var trailer = details.Videos?.Results?
            .FirstOrDefault(v =>
                v.Site == TmdbConstants.VideoSiteYouTube &&
                (v.Type == TmdbConstants.VideoTypeTrailer || v.Type == TmdbConstants.VideoTypeTeaser));

        var trailerUrl = trailer != null
            ? $"{TmdbConstants.YouTubeWatchBaseUrl}{trailer.Key}"
            : null;

        var movie = Movie.Import(
            details.Id,
            details.Title,
            details.Overview,
            details.Runtime ?? 0,
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

        var existingGenres = await context.Genres
            .Where(g => g.ExternalId != null && tmdbGenreIds.Contains(g.ExternalId.Value))
            .ToListAsync(ct);

        foreach (var tmdbGenre in tmdbGenres)
        {
            var genre = existingGenres.FirstOrDefault(g => g.ExternalId == tmdbGenre.Id);

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
