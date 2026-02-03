using System.Reflection;
using Cinema.Application.Genres.Dtos;
using Cinema.Application.Halls.Dtos;
using Cinema.Application.Movies.Dtos;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Mapster;
using Cinema.Application;

namespace Cinema.Api.Modules;

public class MappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        var domainAssembly = typeof(Movie).Assembly;
        var entityTypes = domainAssembly.GetTypes()
            .Where(t => t.IsClass 
                        && !t.IsAbstract 
                        && !t.IsGenericType
                        && t.Namespace != null
                        && t.Namespace.EndsWith("Entities"));

        var configureMethod = typeof(MappingConfig)
            .GetMethod(nameof(ConfigureEntityId), BindingFlags.NonPublic | BindingFlags.Static);

        foreach (var type in entityTypes)
        {
            configureMethod!.MakeGenericMethod(type)
                .Invoke(null, new object[] { config });
        }
        
        config.Scan(
            Assembly.GetExecutingAssembly(), 
            typeof(Cinema.Application.ConfigureServices).Assembly
        );
        
        config.NewConfig<HallTechnology, TechnologyDto>()
            .Map(dest => dest, src => src.Technology);

        config.NewConfig<MovieGenre, GenreDto>()
            .Map(dest => dest, src => src.Genre);

        config.NewConfig<Guid, MovieGenre>()
            .Map(dest => dest.GenreId, src => CreateEntityId<Genre>(src));
    }

    private static void ConfigureEntityId<T>(TypeAdapterConfig config)
    {
        config.NewConfig<EntityId<T>, Guid>()
            .MapWith(id => id.Value);

        config.NewConfig<Guid, EntityId<T>>()
            .MapWith(guid => CreateEntityId<T>(guid));
    }
    
    private static EntityId<T> CreateEntityId<T>(Guid guid)
    {
        return (EntityId<T>)Activator.CreateInstance(typeof(EntityId<T>), guid)!;
    }
}
