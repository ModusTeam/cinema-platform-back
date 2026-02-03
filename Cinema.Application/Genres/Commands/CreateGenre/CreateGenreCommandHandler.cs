using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Genres.Commands.CreateGenre;

public class CreateGenreCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<CreateGenreCommand, Result<int>>
{
    public async Task<Result<int>> Handle(CreateGenreCommand request, CancellationToken ct)
    {
        if (await context.Genres.AnyAsync(g => g.ExternalId == request.ExternalId, ct))
        {
            return Result.Failure<int>(new Error("Genre.Exists", "Genre with this ID already exists"));
        }

        var genre = Genre.Create(request.ExternalId, request.Name);
        
        context.Genres.Add(genre);
        await context.SaveChangesAsync(ct);

        return Result.Success(genre.ExternalId);
    }
}