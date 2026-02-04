using Cinema.Application.Halls.Dtos;
using Cinema.Domain.Entities;
using Mapster;

namespace Cinema.Application.Common.Mappings.Halls;

public class HallMappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Hall, HallDto>()
            .Map(dest => dest.Seats, src => (List<SeatDto>)null)
            .Map(dest => dest.Technologies, src => src.Technologies.Select(t => t.Technology))
            .Map(dest => dest.TotalCapacity, src => src.TotalCapacity);

        config.NewConfig<HallTechnology, TechnologyDto>()
            .Map(dest => dest, src => src.Technology);

        config.NewConfig<Seat, SeatDto>()
            .Map(dest => dest.Row, src => src.RowLabel)
            .Map(dest => dest.SeatTypeName, src => src.SeatType.Name);
    }
}