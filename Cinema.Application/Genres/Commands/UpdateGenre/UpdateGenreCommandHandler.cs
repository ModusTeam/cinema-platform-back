using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Genres.Commands.UpdateGenre;

public class UpdateGenreCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<UpdateGenreCommand, Result>
{
    public async Task<Result> Handle(UpdateGenreCommand request, CancellationToken ct)
    {
        var genre = await context.Genres
            .FirstOrDefaultAsync(g => g.ExternalId == request.ExternalId, ct);

        if (genre == null) return Result.Failure(new Error("Genre.NotFound", "Genre not found"));

        genre.Update(request.Name);
        
        await context.SaveChangesAsync(ct);
        return Result.Success();
    }
}