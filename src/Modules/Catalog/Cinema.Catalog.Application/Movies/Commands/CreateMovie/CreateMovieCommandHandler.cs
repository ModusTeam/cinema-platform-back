using Cinema.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Catalog.Domain.Entities;
using Cinema.Domain.Shared;
using Hangfire;
using MediatR;
using Cinema.Catalog.Application.Movies.Services;
using Microsoft.Extensions.Logging;

namespace Cinema.Catalog.Application.Movies.Commands.CreateMovie;

public class CreateMovieCommandHandler(IApplicationDbContext context, IBackgroundJobClient jobClient,
    ManualMovieReferences references, ILogger<CreateMovieCommandHandler> logger)
    : IRequestHandler<CreateMovieCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateMovieCommand request, CancellationToken ct)
    {
        var genres = await references.ResolveGenresAsync(request.GenreIds, ct);
        if (genres.IsFailure) return Result.Failure<Guid>(genres.Error);
        Result images = await references.ValidateImagesAsync(request.Poster, request.Backdrop, ct);
        if (images.IsFailure) return Result.Failure<Guid>(images.Error);

        var movie = Movie.CreateManual(
            request.Title,
            request.Description,
            request.DurationMinutes,
            request.ReleaseYear,
            request.Status, request.Rating, request.AgeRestriction, request.TrailerUrl
        );

        movie.SetPoster(request.Poster?.SecureUrl, request.Poster?.PublicId);
        movie.SetBackdrop(request.Backdrop?.SecureUrl, request.Backdrop?.PublicId);
        if (request.Cast is not null)
            movie.SetCast(request.Cast.Select(member => new MovieCastMember { Name = member.Name.Trim(), Role = member.Role?.Trim() }));
        foreach (Genre genre in genres.Value) movie.AddGenre(genre);

        context.Movies.Add(movie);
        await context.SaveChangesAsync(ct);
        
        try
        {
            jobClient.Enqueue<IAiEmbeddingService>(s => s.UpdateMovieEmbeddingAsync(movie.Id.Value, CancellationToken.None));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Movie {MovieId} was created, but its optional embedding job could not be queued.", movie.Id.Value);
        }

        return Result.Success(movie.Id.Value);
    }
}