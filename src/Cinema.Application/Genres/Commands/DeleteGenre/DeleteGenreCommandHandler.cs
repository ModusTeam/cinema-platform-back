using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Genres.Commands.DeleteGenre;

public class DeleteGenreCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<DeleteGenreCommand, Result>
{
    public async Task<Result> Handle(DeleteGenreCommand request, CancellationToken ct)
    {
        var genreId = new EntityId<Genre>(request.Id);

        var genre = await context.Genres.FirstOrDefaultAsync(g => g.Id == genreId, ct);

        if (genre == null) 
            return Result.Failure(new Error("Genre.NotFound", "Genre not found"));
        
        context.Genres.Remove(genre);
        await context.SaveChangesAsync(ct);
        
        return Result.Success();
    }
}