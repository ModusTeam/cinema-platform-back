using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Settings;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Data;
using Cinema.Application.Common.Models.Tmdb;

namespace Cinema.Application.Movies.Commands.ImportMovie;

public class ImportMovieCommandHandler(
    IApplicationDbContext context,
    ITmdbApi tmdbApi,               
    IOptions<TmdbSettings> settings
    ) : IRequestHandler<ImportMovieCommand, Result<Guid>>
{
    private readonly TmdbSettings _settings = settings.Value;

    public async Task<Result<Guid>> Handle(ImportMovieCommand request, CancellationToken ct)
    {
        if (await context.Movies.AnyAsync(m => m.ExternalId == request.TmdbId, ct))
        {
            return Result.Failure<Guid>(new Error("Movie.Exists", "Movie already imported."));
        }

        TmdbMovieDetails details;
        try
        {
            details = await tmdbApi.GetMovieDetailsAsync(request.TmdbId, _settings.ApiKey);
        }
        catch (Refit.ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Result.Failure<Guid>(new Error("Tmdb.NotFound", "Movie not found in TMDB."));
        }
        catch (Exception)
        {
            return Result.Failure<Guid>(new Error("Tmdb.Error", "Failed to fetch from TMDB."));
        }
        
        var dbContext = context as DbContext; 
        if (dbContext == null) throw new InvalidOperationException("Context is not EF Core DbContext");

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            
            try
            {
                var posterUrl = !string.IsNullOrEmpty(details.PosterPath) 
                    ? $"{_settings.ImageBaseUrl}{details.PosterPath}" : null;
                var backdropUrl = !string.IsNullOrEmpty(details.BackdropPath) 
                    ? $"{_settings.ImageBaseUrl}{details.BackdropPath}" : null;
                
                var trailer = details.Videos?.Results?
                    .FirstOrDefault(v => v.Site == "YouTube" && (v.Type == "Trailer" || v.Type == "Teaser"));
                var trailerUrl = trailer != null ? $"https://www.youtube.com/watch?v={trailer.Key}" : null;
                
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

                await SyncGenresAsync(movie, details.Genres, ct);

                if (details.Credits?.Cast != null)
                {
                    movie.Cast = details.Credits.Cast
                        .OrderBy(c => c.Order)
                        .Take(12)
                        .Select(c => new MovieCastMember
                        {
                            ExternalId = c.Id,
                            Name = c.Name,
                            Role = c.Character,
                            PhotoUrl = !string.IsNullOrEmpty(c.ProfilePath)
                                ? $"{_settings.ImageBaseUrl}{c.ProfilePath}" : null
                        })
                        .ToList();
                }

                context.Movies.Add(movie);
                await context.SaveChangesAsync(ct);
                
                await transaction.CommitAsync(ct);

                return Result.Success(movie.Id.Value);
            }
            catch (Exception)
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        });
    }

    private async Task SyncGenresAsync(Movie movie, List<TmdbGenreDto> tmdbGenres, CancellationToken ct)
    {
        if (tmdbGenres == null || !tmdbGenres.Any()) return;

        var tmdbGenreIds = tmdbGenres.Select(g => g.Id).ToList();
        
        var existingGenres = await context.Genres
            .Where(g => g.ExternalId != null && tmdbGenreIds.Contains(g.ExternalId.Value))
            .ToListAsync(ct);

        foreach (var tmdbGenre in tmdbGenres)
        {
            var genre = existingGenres.FirstOrDefault(g => g.ExternalId == tmdbGenre.Id);

            if (genre == null)
            {
                genre = Genre.Import(tmdbGenre.Id, tmdbGenre.Name);
                
                context.Genres.Add(genre);
                existingGenres.Add(genre);
            }
            movie.AddGenre(genre);
        }
    }
}