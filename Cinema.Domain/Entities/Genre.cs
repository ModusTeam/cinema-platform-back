using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class Genre
{
    public EntityId<Genre> Id { get; }
    public int ExternalId { get; }
    public string Name { get; private set; }
    public string? Slug { get; set; }
    public ICollection<MovieGenre>? MovieGenres { get; private set; } = [];


    private Genre(
        EntityId<Genre> id,
        int externalId,
        string name,
        string? slug)
    {
        Id = id;
        ExternalId = externalId;
        Name = name;
        Slug = slug;
    }

    public static Genre New(
        EntityId<Genre> id,
        int externalId,
        string name,
        string? slug) => new(id, externalId, name, slug);
}