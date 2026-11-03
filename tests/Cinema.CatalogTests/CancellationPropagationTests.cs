using Cinema.Application.Common.Behaviours;
using Cinema.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Models.Tmdb;
using Cinema.Catalog.Application.Common.Settings;
using Cinema.Catalog.Application.Movies.Queries.SearchTmdb;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Cinema.CatalogTests;

public class CancellationPropagationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validation_pipeline_forwards_token_with_or_without_validators(bool includeValidator)
    {
        InlineValidator<ProbeCommand> validator = new();
        validator.RuleFor(command => command.RequestId).NotEmpty();
        var behavior = new ValidationBehavior<ProbeCommand, ProbeResponse>(includeValidator ? [validator] : []);
        CancellationToken observed = default;
        await behavior.Handle(new(Guid.NewGuid()), token =>
        {
            observed = token;
            return Task.FromResult(new ProbeResponse("completed"));
        }, TestContext.Current.CancellationToken);
        observed.Should().Be(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Idempotency_passes_token_to_next_and_does_not_cache_cancelled_execution()
    {
        IDistributedCache cache = Substitute.For<IDistributedCache>();
        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        var behavior = new IdempotencyBehavior<ProbeCommand, ProbeResponse>(cache,
            NullLogger<IdempotencyBehavior<ProbeCommand, ProbeResponse>>.Instance);
        using CancellationTokenSource cancellation = new();
        CancellationToken observed = default;
        Func<Task> action = () => behavior.Handle(new(Guid.NewGuid()), token =>
        {
            observed = token;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new ProbeResponse("completed"));
        }, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        observed.Should().Be(cancellation.Token);
        await cache.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(),
            Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelled_cache_read_does_not_start_execution()
    {
        IDistributedCache cache = Substitute.For<IDistributedCache>();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        cache.GetAsync(Arg.Any<string>(), cancellation.Token).Returns(Task.FromCanceled<byte[]?>(cancellation.Token));
        var behavior = new IdempotencyBehavior<ProbeCommand, ProbeResponse>(cache,
            NullLogger<IdempotencyBehavior<ProbeCommand, ProbeResponse>>.Instance);
        bool executed = false;
        Func<Task> action = () => behavior.Handle(new(Guid.NewGuid()), _ =>
        {
            executed = true;
            return Task.FromResult(new ProbeResponse("unexpected"));
        }, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        executed.Should().BeFalse();
    }

    [Fact]
    public async Task Successful_execution_caches_original_response_and_token()
    {
        IDistributedCache cache = Substitute.For<IDistributedCache>();
        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        var behavior = new IdempotencyBehavior<ProbeCommand, ProbeResponse>(cache,
            NullLogger<IdempotencyBehavior<ProbeCommand, ProbeResponse>>.Instance);
        Guid requestId = Guid.NewGuid();
        var response = await behavior.Handle(new(requestId), token =>
        {
            token.Should().Be(TestContext.Current.CancellationToken);
            return Task.FromResult(new ProbeResponse("completed"));
        }, TestContext.Current.CancellationToken);
        response.Value.Should().Be("completed");
        await cache.Received(1).SetAsync($"idempotency:{requestId}",
            Arg.Is<byte[]>(bytes => System.Text.Encoding.UTF8.GetString(bytes) == "{\"Value\":\"completed\"}"),
            Arg.Is<DistributedCacheEntryOptions>(options => options.AbsoluteExpirationRelativeToNow == TimeSpan.FromHours(24)),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Idempotency_retains_successful_cached_response_without_reexecuting_handler()
    {
        IDistributedCache cache = Substitute.For<IDistributedCache>();
        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(System.Text.Encoding.UTF8.GetBytes("{\"Value\":\"completed\"}"));
        var behavior = new IdempotencyBehavior<ProbeCommand, ProbeResponse>(cache,
            NullLogger<IdempotencyBehavior<ProbeCommand, ProbeResponse>>.Instance);
        bool executed = false;
        ProbeResponse result = await behavior.Handle(new(Guid.NewGuid()), _ =>
        {
            executed = true;
            return Task.FromResult(new ProbeResponse("unexpected"));
        }, TestContext.Current.CancellationToken);

        result.Value.Should().Be("completed");
        executed.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tmdb_cancellation_propagates_and_is_not_cached_as_success(bool cancelBeforeCall)
    {
        ITmdbApi api = Substitute.For<ITmdbApi>();
        using MemoryCache cache = new(new MemoryCacheOptions());
        SearchTmdbQueryHandler handler = CreateHandler(api, cache);
        using CancellationTokenSource cancellation = new();
        if (cancelBeforeCall) cancellation.Cancel();
        api.SearchMoviesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<TmdbSearchResponse>(cancellation.Token);
            });

        Func<Task> action = () => handler.Handle(new("Cinema"), cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        if (cancelBeforeCall)
            await api.DidNotReceive().SearchMoviesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        else
            await api.Received(1).SearchMoviesAsync("Cinema", "test-key", cancellation.Token);

        api.SearchMoviesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResponse { Results = [new() { Id = 42, Title = "Cinema", ReleaseDate = "2026-01-01" }] });
        var result = await handler.Handle(new("Cinema"), TestContext.Current.CancellationToken);
        result.Should().ContainSingle().Which.TmdbId.Should().Be(42);
    }

    [Fact]
    public async Task Tmdb_success_preserves_mapping_and_cache()
    {
        ITmdbApi api = Substitute.For<ITmdbApi>();
        using MemoryCache cache = new(new MemoryCacheOptions());
        api.SearchMoviesAsync("Cinema", "test-key", Arg.Any<CancellationToken>())
            .Returns(new TmdbSearchResponse { Results = [new() { Id = 42, Title = "Cinema", ReleaseDate = "2026-01-01", PosterPath = "/poster.jpg" }] });
        SearchTmdbQueryHandler handler = CreateHandler(api, cache);
        var result = await handler.Handle(new("Cinema"), TestContext.Current.CancellationToken);
        result.Should().ContainSingle().Which.Should().Be(new TmdbSearchResult(42, "Cinema", "2026", "https://images.example/poster.jpg"));
        (await handler.Handle(new("Cinema"), TestContext.Current.CancellationToken)).Should().BeSameAs(result);
        await api.Received(1).SearchMoviesAsync("Cinema", "test-key", TestContext.Current.CancellationToken);
    }

    private static SearchTmdbQueryHandler CreateHandler(ITmdbApi api, IMemoryCache cache) => new(api, cache,
        Options.Create(new TmdbSettings { ApiKey = "test-key", ImageBaseUrl = "https://images.example" }),
        NullLogger<SearchTmdbQueryHandler>.Instance);

    public record ProbeCommand(Guid RequestId) : IIdempotentCommand;
    public record ProbeResponse(string Value);
}
