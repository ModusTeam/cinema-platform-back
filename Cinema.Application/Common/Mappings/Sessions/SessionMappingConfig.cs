using Cinema.Application.Sessions.Dtos;
using Cinema.Domain.Entities;
using Mapster;

namespace Cinema.Application.Common.Mappings.Sessions;

public class SessionMappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Session, SessionDto>()
            .Map(dest => dest.Status, src => src.Status.ToString())
            .Map(dest => dest.MovieTitle, src => src.Movie.Title)
            .Map(dest => dest.HallName, src => src.Hall.Name)
            .Map(dest => dest.PricingName, src => src.Pricing.Name);
    }
}