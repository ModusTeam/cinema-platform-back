using Cinema.Application.Sessions.Dtos;
using Cinema.Domain.Entities;
using Mapster;

namespace Cinema.Application.Common.Mappings.Sessions;

public class SessionMappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Session, SessionDto>()
            .Map(dest => dest.Id, src => src.Id.Value)
            .Map(dest => dest.MovieId, src => src.MovieId.Value)
            .Map(dest => dest.HallId, src => src.HallId.Value)
            .Map(dest => dest.PricingId, src => src.PricingId.Value)
            .Map(dest => dest.Status, src => src.Status.ToString())
            .Map(dest => dest.MovieTitle, src => src.Movie != null ? src.Movie.Title : string.Empty)
            .Map(dest => dest.HallName, src => src.Hall != null ? src.Hall.Name : string.Empty)
            .Map(dest => dest.PricingName, src => src.Pricing != null ? src.Pricing.Name : string.Empty);
    }
}