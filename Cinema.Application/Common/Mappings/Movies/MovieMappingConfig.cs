using Cinema.Application.Movies.Dtos;
using Cinema.Domain.Entities;
using Mapster;

namespace Cinema.Application.Common.Mappings.Movies;

public class MovieMappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Movie, MovieDto>()
            .Map(dest => dest.Id, src => src.Id.Value)
            .Map(dest => dest.Status, src => (int)src.Status)
            .Map(dest => dest.Genres, src => src.MovieGenres.Select(mg => mg.Genre.Name))
            .Map(dest => dest.Cast, src => src.Cast);
        
        config.NewConfig<Movie, MovieListDto>()
            .Map(dest => dest.Id, src => src.Id.Value)
            .Map(dest => dest.Status, src => (int)src.Status)
            .Map(dest => dest.Genres, src => src.MovieGenres.Select(mg => mg.Genre.Name));
        
        config.NewConfig<MovieCastMember, ActorDto>()
            .Map(dest => dest.Name, src => src.Name)
            .Map(dest => dest.Role, src => src.Role)
            .Map(dest => dest.PhotoUrl, src => src.PhotoUrl);
    }
}