using Cinema.Application.Orders.Dtos;
using Cinema.Domain.Entities;
using Mapster;

namespace Cinema.Application.Common.Mappings.Orders;

public class OrderMappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Order, OrderDto>()
            .Map(dest => dest.Status, src => src.Status.ToString());

        config.NewConfig<Ticket, TicketDto>()
            .Map(dest => dest.SecretCode, src => src.Id.Value.ToString().Substring(0, 8).ToUpper())
            .Map(dest => dest.Status, src => src.TicketStatus.ToString())
            .Map(dest => dest.MovieTitle, src => src.Session.Movie.Title)
            .Map(dest => dest.PosterUrl, src => src.Session.Movie.PosterUrl)
            .Map(dest => dest.HallName, src => src.Session.Hall.Name)
            .Map(dest => dest.RowLabel, src => src.Seat.RowLabel)
            .Map(dest => dest.SeatNumber, src => src.Seat.Number)
            .Map(dest => dest.SeatType, src => src.Seat.SeatType.Name);
    }
}