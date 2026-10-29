using Cinema.Application.Common.Interfaces;
using Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Commands;
using Cinema.Catalog.Application.Movies.Services;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Errors;
using Cinema.Domain.Common;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Handlers;

public class UpdateMovieImagesCommandHandler(IApplicationDbContext context, ManualMovieReferences references)
    : IRequestHandler<UpdateMovieImagesCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(UpdateMovieImagesCommand request, CancellationToken ct)
    {
        var movieId = new EntityId<Movie>(request.Id);
        var movie = await context.Movies.FirstOrDefaultAsync(m => m.Id == movieId, ct);
        if (movie is null) return Result.Failure<Guid>(MovieErrors.NotFound);

        Result images = await references.ValidateImagesAsync(request.Poster, request.Backdrop, ct);
        if (images.IsFailure) return Result.Failure<Guid>(images.Error);
        // Legacy URLs can retain an existing image or use TMDB; managed images require verified references.
        if (!ValidLegacyUrl(request.PosterUrl, movie.PosterUrl) || !ValidLegacyUrl(request.BackdropUrl, movie.BackdropUrl))
            return Result.Failure<Guid>(new Error("MovieImage.Validation.Reference", "Use an uploaded image reference for managed images, or an HTTPS TMDB image URL."));

        if (request.SetPosterReference || request.Poster is not null)
            movie.SetPoster(request.Poster?.SecureUrl, request.Poster?.PublicId);
        else if (request.SetPosterUrl || request.PosterUrl is not null)
            movie.SetPoster(request.PosterUrl, request.PosterUrl == movie.PosterUrl ? movie.PosterPublicId : null);

        if (request.SetBackdropReference || request.Backdrop is not null)
            movie.SetBackdrop(request.Backdrop?.SecureUrl, request.Backdrop?.PublicId);
        else if (request.SetBackdropUrl || request.BackdropUrl is not null)
            movie.SetBackdrop(request.BackdropUrl, request.BackdropUrl == movie.BackdropUrl ? movie.BackdropPublicId : null);

        if (request.SetTrailerUrl || request.TrailerUrl is not null) movie.SetTrailer(request.TrailerUrl);

        // No asset is deleted on replacement. Retained/abandoned assets follow the maintenance cleanup policy.
        await context.SaveChangesAsync(ct);
        return Result.Success(movie.Id.Value);
    }

    private static bool ValidLegacyUrl(string? url, string? current) => url is null || url == current ||
        (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == "https" &&
         uri.Host == "image.tmdb.org" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
         uri.AbsolutePath.StartsWith("/t/p/", StringComparison.Ordinal));
}
