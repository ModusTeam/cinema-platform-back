using System.Collections.Frozen;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Cinema.Infrastructure.Services;

public class SeatTypeProvider(IServiceProvider serviceProvider) : ISeatTypeProvider
{
    private FrozenDictionary<EntityId<SeatType>, SeatType>? _cache;
    private readonly object _lock = new();

    private void EnsureInitialized()
    {
        if (_cache != null) return;

        lock (_lock)
        {
            if (_cache != null) return;
            
            using var scope = serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            
            var types = context.SeatTypes.ToList();
            _cache = types.ToFrozenDictionary(k => k.Id, v => v);
        }
    }

    public SeatType? Get(EntityId<SeatType> id)
    {
        EnsureInitialized();
        return _cache!.TryGetValue(id, out var type) ? type : null;
    }

    public string GetName(EntityId<SeatType> id)
    {
        EnsureInitialized();
        return _cache!.TryGetValue(id, out var type) ? type.Name : "Unknown";
    }
}