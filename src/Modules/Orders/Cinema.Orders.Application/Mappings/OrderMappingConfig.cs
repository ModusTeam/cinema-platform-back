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
    }
}
