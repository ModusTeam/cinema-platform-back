using Cinema.Application.Orders.Dtos;
using Cinema.Domain.Entities;
using Mapster;

namespace Cinema.Application.Common.Mappings.Orders;

public class OrderMappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Order, OrderDto>()
            .Map(dest => dest.Id, src => src.Id.Value)
            .Map(dest => dest.TotalAmount, src => src.TotalAmount)
            .Map(dest => dest.CreatedAt, src => src.BookingDate)
            .Map(dest => dest.Status, src => src.Status.ToString())
            .Map(dest => dest.Tickets, src => src.Tickets);
        
        config.NewConfig<Ticket, TicketDto>()
            .Map(dest => dest.Id, src => src.Id.Value)

            .Map(dest => dest.Price, src => src.PriceSnapshot)
            .Map(dest => dest.Status, src => src.TicketStatus.ToString())
            .Map(dest => dest.MovieTitle, src => src.Session.Movie.Title)
            .Map(dest => dest.PosterUrl, src => src.Session.Movie.PosterUrl)
            .Map(dest => dest.SessionStart, src => src.Session.StartTime)
            .Map(dest => dest.HallName, src => src.Session.Hall.Name)
            .Map(dest => dest.RowLabel, src => src.Seat.RowLabel)
            .Map(dest => dest.SeatNumber, src => src.Seat.Number)
            .Map(dest => dest.SeatType, src => src.Seat.SeatType.Name)
            .Map(dest => dest.SecretCode, src => src.Id.Value.ToString().Substring(0, 8).ToUpper());
    }
}