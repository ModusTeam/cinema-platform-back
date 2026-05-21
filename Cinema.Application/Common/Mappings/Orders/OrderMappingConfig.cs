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
            .Map(dest => dest.MovieTitle, src => src.Session != null && src.Session.Movie != null ? src.Session.Movie.Title : string.Empty)
            .Map(dest => dest.PosterUrl, src => src.Session != null && src.Session.Movie != null ? src.Session.Movie.PosterUrl : null)
            .Map(dest => dest.SessionStart, src => src.Session != null ? src.Session.StartTime : default)
            .Map(dest => dest.HallName, src => src.Session != null && src.Session.Hall != null ? src.Session.Hall.Name : string.Empty)
            .Map(dest => dest.RowLabel, src => src.Seat != null ? src.Seat.RowLabel : string.Empty)
            .Map(dest => dest.SeatNumber, src => src.Seat != null ? src.Seat.Number : 0)
            .Map(dest => dest.SeatType, src => src.Seat != null && src.Seat.SeatType != null ? src.Seat.SeatType.Name : string.Empty)
            .Map(dest => dest.SecretCode, src => src.Id.Value.ToString().Substring(0, 8).ToUpper());
    }
}