using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Hangfire;
using MediatR;

namespace Cinema.Application.Movies.Commands.CreateMovie;

public class CreateMovieCommandHandler(IApplicationDbContext context, IBackgroundJobClient jobClient) 
    : IRequestHandler<CreateMovieCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateMovieCommand request, CancellationToken ct)
    {
        var movie = Movie.CreateManual(
            request.Title,
            request.Description,
            request.DurationMinutes,
            request.ReleaseYear,
            request.Status
        );

        context.Movies.Add(movie);
        await context.SaveChangesAsync(ct);
        
        jobClient.Enqueue<IAiEmbeddingService>(s => s.UpdateMovieEmbeddingAsync(movie.Id.Value, CancellationToken.None));

        return Result.Success(movie.Id.Value);
    }
}