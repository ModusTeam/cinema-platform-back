using Cinema.Application.Genres.Dtos;
using Cinema.Application.Pricings.Dtos;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Mapster;

namespace Cinema.Application.Common.Mappings.Catalog;

public class CatalogMappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {

        config.NewConfig<MovieGenre, GenreDto>()
            .Map(dest => dest, src => src.Genre);
        
        config.NewConfig<Guid, MovieGenre>()
            .Map(dest => dest.GenreId, src => new EntityId<Genre>(src));
        
        
        config.NewConfig<Pricing, PricingDetailsDto>()
            .Map(dest => dest.Items, src => (List<PricingItemDto>)null);

        config.NewConfig<PricingItem, PricingItemDto>()
            .Map(dest => dest.SeatTypeName, src => src.SeatType.Name);

        config.NewConfig<Pricing, PricingLookupDto>()
            .IgnoreNonMapped(true);
    }
}