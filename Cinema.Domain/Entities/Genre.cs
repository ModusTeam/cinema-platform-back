using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class Genre
{
    public EntityId<Genre> Id { get; }
    public int ExternalId { get; private set; }
    public string Name { get; private set; }
    public string? Slug { get; private set; }
    
    public ICollection<MovieGenre> MovieGenres { get; private set; } = [];


    private Genre(EntityId<Genre> id, int externalId, string name, string slug)
    {
        Id = id;
        ExternalId = externalId;
        Name = name;
        Slug = slug;
    }

    public static Genre Import(int externalId, string name)
    {
        var slug = name.ToLower().Trim().Replace(" ", "-");
        return new Genre(EntityId<Genre>.New(), externalId, name, slug);
    }
}