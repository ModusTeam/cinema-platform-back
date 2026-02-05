using Cinema.Domain.Entities;

namespace Cinema.Application.Sessions.Dtos;

public record SessionDto
{
    public Guid Id { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public string Status { get; init; } = string.Empty;
    public Guid MovieId { get; init; }
    public string MovieTitle { get; init; } = string.Empty;
    public Guid HallId { get; init; }
    public string HallName { get; init; } = string.Empty;
    public Guid PricingId { get; init; }
    public string PricingName { get; init; } = string.Empty;

    public List<Guid> OccupiedSeatIds { get; init; } = [];

    public static SessionDto FromDomainModel(Session session)
    {
        return new SessionDto
        {
            Id = session.Id.Value,
            StartTime = session.StartTime,
            EndTime = session.EndTime,
            Status = session.Status.ToString(),
            MovieId = session.MovieId.Value,
            MovieTitle = session.Movie?.Title ?? "Unknown Movie",
            HallId = session.HallId.Value,
            HallName = session.Hall?.Name ?? "Unknown Hall",
            PricingId = session.PricingId.Value,
            PricingName = session.Pricing?.Name ?? "Unknown Pricing",
            OccupiedSeatIds = []
        };
    }
}