using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Genres.Commands.DeleteGenre;

public class DeleteGenreCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<DeleteGenreCommand, Result>
{
    public async Task<Result> Handle(DeleteGenreCommand request, CancellationToken ct)
    {
        var genre = await context.Genres
            .FirstOrDefaultAsync(g => g.ExternalId == request.ExternalId, ct);

        if (genre == null) return Result.Failure(new Error("Genre.NotFound", "Genre not found"));

        context.Genres.Remove(genre);
        await context.SaveChangesAsync(ct);
        
        return Result.Success();
    }
}