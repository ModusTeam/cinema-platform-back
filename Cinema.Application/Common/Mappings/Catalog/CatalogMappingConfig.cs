using Cinema.Application.Genres.Dtos;
using Cinema.Application.Movies.Dtos;
using Cinema.Application.Pricings.Dtos;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Mapster;

namespace Cinema.Application.Common.Mappings.Catalog;

public class CatalogMappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Pricing, PricingDetailsDto>()
            .Map(dest => dest.Id, src => src.Id.Value)
            .Map(dest => dest.Name, src => src.Name)
            .Map(dest => dest.Items, src => (List<PricingItemDto>)null);
        
        config.NewConfig<PricingItem, PricingItemDto>()
            .Map(dest => dest.Id, src => src.Id.Value)
            .Map(dest => dest.SeatTypeId, src => src.SeatTypeId.Value)
            .Map(dest => dest.SeatTypeName, src => src.SeatType.Name);
        
        config.NewConfig<Pricing, PricingLookupDto>()
            .Map(dest => dest.Id, src => src.Id.Value)
            .Map(dest => dest.Name, src => src.Name);
    }
}