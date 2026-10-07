using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class Genre
{
    public EntityId<Genre> Id { get; private set; }
    public int? ExternalId { get; private set; }
    public string Name { get; private set; }
    public string? Slug { get; private set; }
    
    public ICollection<MovieGenre> MovieGenres { get; private set; } = [];

    private Genre(EntityId<Genre> id, int? externalId, string name, string slug)
    {
        Id = id;
        ExternalId = externalId;
        Name = name;
        Slug = slug;
    }

    public static Genre Import(int externalId, string name)
    {
        var slug = GenerateSlug(name);
        return new Genre(EntityId<Genre>.New(), externalId, name, slug);
    }

    public static Genre Create(int? externalId, string name)
    {
        var slug = GenerateSlug(name);
        return new Genre(EntityId<Genre>.New(), externalId, name, slug);
    }

    public void Update(string name)
    {
        Name = name;
        Slug = GenerateSlug(name);
    }

    private static string GenerateSlug(string name) 
        => name.ToLower().Trim().Replace(" ", "-");
}