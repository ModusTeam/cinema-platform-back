using Cinema.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Errors;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Catalog.Application.Movies.Commands.DeleteMovie;

public class DeleteMovieCommandHandler(IApplicationDbContext context)
    : IRequestHandler<DeleteMovieCommand, Result>
{
    public async Task<Result> Handle(DeleteMovieCommand request, CancellationToken ct)
    {
        var movieId = new EntityId<Movie>(request.Id);
        var movie = await context.Movies.FirstOrDefaultAsync(m => m.Id == movieId, ct);

        if (movie is null) return Result.Failure(MovieErrors.NotFound);

        movie.Delete();
        await context.SaveChangesAsync(ct);

        return Result.Success();
    }
}