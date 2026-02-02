using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Cinema.Application.Movies.Commands.ImportMovie;

public class ImportMovieCommandHandler(
    IApplicationDbContext context,
    ITmdbService tmdbService,
    IConfiguration config)
    : IRequestHandler<ImportMovieCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ImportMovieCommand request, CancellationToken ct)
    {
        var exists = await context.Movies.AnyAsync(m => m.ExternalId == request.TmdbId, ct);
        if (exists) return Result.Failure<Guid>(new Error("Movie.Exists", "Movie already imported."));
        
        var details = await tmdbService.GetMovieDetailsAsync(request.TmdbId);
        if (details == null) return Result.Failure<Guid>(new Error("Tmdb.NotFound", "Movie not found in TMDB."));
        
        var imgBase = config["Tmdb:ImageBaseUrl"] ?? "https://image.tmdb.org/t/p/original";
        var posterUrl = !string.IsNullOrEmpty(details.PosterPath) ? $"{imgBase}{details.PosterPath}" : null;
        var backdropUrl = !string.IsNullOrEmpty(details.BackdropPath) ? $"{imgBase}{details.BackdropPath}" : null;
        
        string? trailerUrl = null;
        if (details.Videos?.Results != null)
        {
            var trailer = details.Videos.Results
                .FirstOrDefault(v => v.Site == "YouTube" && v.Type == "Trailer");
            
            trailer ??= details.Videos.Results.FirstOrDefault(v => v.Site == "YouTube");

            if (trailer != null)
            {
                trailerUrl = $"https://www.youtube.com/watch?v={trailer.Key}";
            }
        }
        
        DateTime? releaseDate = DateTime.TryParse(details.ReleaseDate, out var d) ? d : null;
        
        var movie = Movie.Import(
            details.Id,
            details.Title,
            details.Overview,
            details.Runtime ?? 0,
            (decimal)details.VoteAverage,
            releaseDate,
            posterUrl,
            backdropUrl,
            trailerUrl
        );
        
        var tmdbGenreIds = details.Genres.Select(g => g.Id).ToList();
        var existingGenres = await context.Genres.Where(g => tmdbGenreIds.Contains(g.ExternalId)).ToListAsync(ct);
        foreach (var tmdbGenre in details.Genres)
        {
            var genre = existingGenres.FirstOrDefault(g => g.ExternalId == tmdbGenre.Id);
            if (genre == null)
            {
                genre = Genre.Import(tmdbGenre.Id, tmdbGenre.Name);
                context.Genres.Add(genre);
            }
            movie.AddGenre(genre);
        }
        
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
                        ? $"{imgBase}{c.ProfilePath}" : null
                })
                .ToList();
        }

        context.Movies.Add(movie);
        await context.SaveChangesAsync(ct);

        return Result.Success(movie.Id.Value);
    }
}