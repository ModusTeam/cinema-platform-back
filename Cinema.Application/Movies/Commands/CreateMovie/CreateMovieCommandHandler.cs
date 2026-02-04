using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Movies.Commands.CreateMovie;

public class CreateMovieCommandHandler(IApplicationDbContext context) 
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

        return Result.Success(movie.Id.Value);
    }
}