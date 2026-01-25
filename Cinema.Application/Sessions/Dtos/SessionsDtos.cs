using Cinema.Domain.Entities;

namespace Cinema.Application.Sessions.Dtos;

public record SessionDto(
    Guid Id,
    DateTime StartTime,
    DateTime EndTime,
    string Status,
    Guid MovieId,
    string MovieTitle,
    Guid HallId,
    string HallName
)
{
    public static SessionDto FromDomainModel(Session session)
        => new(
            session.Id.Value,
            session.StartTime,
            session.EndTime,
            session.Status.ToString(),
            session.MovieId.Value,
            session.Movie?.Title ?? "Unknown Movie",
            session.HallId.Value,
            session.Hall?.Name ?? "Unknown Hall"
        );
}