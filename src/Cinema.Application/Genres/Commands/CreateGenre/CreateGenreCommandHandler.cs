using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Genres.Commands.CreateGenre;

public class CreateGenreCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<CreateGenreCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateGenreCommand request, CancellationToken ct)
    {
        var normalizedName = request.Name.Trim();
        
        var nameExists = await context.Genres
            .AnyAsync(g => g.Name.ToLower() == normalizedName.ToLower(), ct);
            
        if (nameExists)
        {
            return Result.Failure<Guid>(new Error("Genre.DuplicateName", $"Genre with name '{normalizedName}' already exists."));
        }
        
        if (request.ExternalId.HasValue)
        {
            var idExists = await context.Genres
                .AnyAsync(g => g.ExternalId == request.ExternalId, ct);

            if (idExists)
            {
                return Result.Failure<Guid>(new Error("Genre.DuplicateId", $"Genre with ExternalId '{request.ExternalId}' already exists."));
            }
        }

        var genre = Genre.Create(request.ExternalId, normalizedName);
        
        context.Genres.Add(genre);
        await context.SaveChangesAsync(ct);

        return Result.Success(genre.Id.Value);
    }
}