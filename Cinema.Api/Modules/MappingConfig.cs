using System.Reflection;
using Cinema.Application.Halls.Dtos;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Mapster;

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

        config.Scan(Assembly.GetExecutingAssembly());
        config.NewConfig<HallTechnology, TechnologyDto>()
            .Map(dest => dest, src => src.Technology);
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