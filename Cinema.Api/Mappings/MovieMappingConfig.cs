using Cinema.Api.DTOs.Movies;
using Cinema.Domain.Entities;
using Mapster;

namespace Cinema.Api.Mappings;

public class MovieMappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        config.NewConfig<Genre, string>()
            .MapWith(src => src.Name);

        config.NewConfig<Movie, MovieDto>()
            .Map(dest => dest.Id,           src => src.Id.Value)
            .Map(dest => dest.Status,       src => (int)src.Status)
            .Map(dest => dest.AgeRestriction, src => src.AgeRestriction)
            .Map(dest => dest.PosterUrl,    src => src.PosterUrl)
            .Map(dest => dest.BackdropUrl,  src => src.BackdropUrl)
            .Map(dest => dest.TrailerUrl,   src => src.TrailerUrl)
            .Map(dest => dest.Genres,       src => src.MovieGenres != null 
                ? src.MovieGenres.Where(mg => mg.Genre != null).Select(mg => mg.Genre!.Name).ToList() 
                : new List<string>())
            .Map(dest => dest.Cast,         src => src.Cast);

        config.NewConfig<Movie, MovieListDto>()
            .Map(dest => dest.Id,             src => src.Id.Value)
            .Map(dest => dest.Status,         src => (int)src.Status)
            .Map(dest => dest.PosterUrl,      src => src.PosterUrl)
            .Map(dest => dest.BackdropUrl,    src => src.BackdropUrl)
            .Map(dest => dest.Genres,         src => src.MovieGenres != null 
                ? src.MovieGenres.Where(mg => mg.Genre != null).Select(mg => mg.Genre!.Name).ToList() 
                : new List<string>())
            .Map(dest => dest.AgeRestriction, src => src.AgeRestriction);

        config.NewConfig<MovieCastMember, ActorDto>()
            .Map(dest => dest.Name,     src => src.Name)
            .Map(dest => dest.Role,     src => src.Role)
            .Map(dest => dest.PhotoUrl, src => src.PhotoUrl);
    }
}
