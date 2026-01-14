using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class Movie : BaseEntity
{
    public required string Title { get; set; }
    public string? Description { get; set; }
    public int DurationMinutes { get; set; }
    public decimal Rating { get; set; }
    public string? Actor { get; set; } 
    public string? ImgUrl { get; set; }
    public string? VideoUrl { get; set; }

    public ICollection<MovieGenre> MovieGenres { get; set; } = new List<MovieGenre>();
    public ICollection<Session> Sessions { get; set; } = new List<Session>();
}