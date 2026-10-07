using System.Reflection;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Mapster;

namespace Cinema.Application.Common.Mappings.Common;

public class InfrastructureMappingConfig : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        var domainAssembly = typeof(Movie).Assembly;
        var entityTypes = domainAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericType 
                        && t.Namespace != null && t.Namespace.EndsWith("Entities"));

        var configureMethod = typeof(InfrastructureMappingConfig)
            .GetMethod(nameof(ConfigureEntityId), BindingFlags.NonPublic | BindingFlags.Static);

        foreach (var type in entityTypes)
        {
            configureMethod!.MakeGenericMethod(type).Invoke(null, new object[] { config });
        }
    }

    private static void ConfigureEntityId<T>(TypeAdapterConfig config)
    {
        config.NewConfig<EntityId<T>, Guid>().MapWith(id => id.Value);
        config.NewConfig<Guid, EntityId<T>>().MapWith(guid => CreateEntityId<T>(guid));
    }

    private static EntityId<T> CreateEntityId<T>(Guid guid)
    {
        return (EntityId<T>)Activator.CreateInstance(typeof(EntityId<T>), guid)!;
    }
}