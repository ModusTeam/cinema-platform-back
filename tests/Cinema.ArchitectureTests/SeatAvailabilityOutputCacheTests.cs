using System.Net.Http.Json;
using Cinema.Api.Controllers;
using Cinema.Api.Modules;
using Cinema.Application.Sessions.Queries.GetSessionSeats;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinema.ArchitectureTests;

public class SeatAvailabilityOutputCacheTests
{
    [Fact]
    public async Task Seat_Endpoint_Bypasses_Existing_OutputCache_And_Does_Not_Store_Responses()
    {
        using RecordingStore store = new();
        AvailabilityState legacyState = new();
        string address;
        string path = $"/api/sessions/{legacyState.SessionId}/seats";

        // Simulate the old deployment and keep its actual serialized cache entry.
        await using (WebApplication legacy = await StartAsync(store, legacyState, null, legacy: true))
        {
            address = legacy.Urls.Single();
            using HttpClient client = new() { BaseAddress = new Uri(address) };
            Assert.True((await ReadAsync(client, path)).Seats.Single().IsAvailable);
            legacyState.Available = false;
            Assert.True((await ReadAsync(client, path)).Seats.Single().IsAvailable);
            Assert.Equal(1, legacyState.Reads);
            Assert.True(store.Writes > 0);
            await legacy.StopAsync(TestContext.Current.CancellationToken);
        }

        AvailabilityState currentState = new() { SessionId = legacyState.SessionId, Available = false };
        await using WebApplication current = await StartAsync(store, currentState, address, legacy: false);
        using HttpClient currentClient = new() { BaseAddress = new Uri(address) };
        int lookups = store.Lookups;
        int writes = store.Writes;
        Assert.False((await ReadAsync(currentClient, path)).Seats.Single().IsAvailable);
        currentState.Available = true;
        Assert.True((await ReadAsync(currentClient, path)).Seats.Single().IsAvailable);
        Assert.Equal(2, currentState.Reads);
        Assert.Equal(lookups, store.Lookups);
        Assert.Equal(writes, store.Writes);

        // The production base policy still caches unrelated anonymous endpoints.
        string first = await currentClient.GetStringAsync("/cached-control", TestContext.Current.CancellationToken);
        string second = await currentClient.GetStringAsync("/cached-control", TestContext.Current.CancellationToken);
        Assert.Equal("1", first);
        Assert.Equal(first, second);
    }

    private static async Task<SessionSeatsVm> ReadAsync(HttpClient client, string path)
        => await client.GetFromJsonAsync<SessionSeatsVm>(path, TestContext.Current.CancellationToken)
           ?? throw new InvalidOperationException("Missing seat response");

    private static async Task<WebApplication> StartAsync(
        RecordingStore store, AvailabilityState state, string? address, bool legacy)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(address ?? "http://127.0.0.1:0");
        builder.Services.AddOutputCache(DependencyInjection.ConfigureOutputCaching);
        builder.Services.AddSingleton<IOutputCacheStore>(store);
        builder.Services.AddSingleton(state);
        builder.Services.AddMediatR(configuration =>
            configuration.RegisterServicesFromAssemblyContaining<SeatAvailabilityOutputCacheTests>());
        builder.Services.AddControllers().AddApplicationPart(typeof(SessionsController).Assembly);
        WebApplication app = builder.Build();
        app.UseOutputCache();
        if (legacy)
            app.MapGet("/api/sessions/{id}/seats", () => state.Read());
        else
            app.MapControllers();
        int controlReads = 0;
        app.MapGet("/cached-control", () => Interlocked.Increment(ref controlReads).ToString());
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    public sealed class AvailabilityState
    {
        public Guid SessionId { get; init; } = Guid.NewGuid();
        public bool Available { get; set; } = true;
        public int Reads { get; private set; }
        public SessionSeatsVm Read()
        {
            Reads++;
            return new(SessionId, Guid.Empty, "Cache test", DateTime.UnixEpoch,
                [new(Guid.Empty, 1, 1, "A", 1, 1, "Standard", 400m, Available, !Available, false)]);
        }
    }

    public sealed class SeatHandler(AvailabilityState state)
        : IRequestHandler<GetSessionSeatsQuery, Result<SessionSeatsVm>>
    {
        public Task<Result<SessionSeatsVm>> Handle(GetSessionSeatsQuery request, CancellationToken cancellationToken)
            => Task.FromResult(Result.Success(state.Read()));
    }

    private sealed class RecordingStore : IOutputCacheStore, IDisposable
    {
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        public int Lookups { get; private set; }
        public int Writes { get; private set; }
        public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
        {
            Lookups++;
            return ValueTask.FromResult(cache.Get<byte[]>(key));
        }
        public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
        {
            Writes++;
            cache.Set(key, value, validFor);
            return ValueTask.CompletedTask;
        }
        public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
            => throw new NotSupportedException("No tag eviction in this test");
        public void Dispose() => cache.Dispose();
    }
}
