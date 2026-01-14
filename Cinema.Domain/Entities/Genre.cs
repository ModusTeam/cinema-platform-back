using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class Genre : BaseEntity
{
    public required string Name { get; set; }
    public string? Slug { get; set; }
    public ICollection<MovieGenre> MovieGenres { get; set; } = new List<MovieGenre>();
}