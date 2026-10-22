using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Cinema.Api.Hubs;
using Cinema.Api.Modules;
using Cinema.Api.Services;
using Cinema.Application.Common.Interfaces;
using Cinema.Booking.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Cinema.BookingTests;

public class TicketHubIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_Delivers_Json_Events_Only_To_Joined_Sessions_And_Target_Users(bool redisBackplane)
    {
        string? redis = null;
        if (redisBackplane)
        {
            redis = Environment.GetEnvironmentVariable("CINEMA_TEST_REDIS_CONNECTION");
            if (string.IsNullOrWhiteSpace(redis))
                Assert.Skip("Set CINEMA_TEST_REDIS_CONNECTION for the two-host Redis/SignalR integration test.");
        }

        string prefix = $"CinemaPlatform:test:{Guid.NewGuid():N}";
        await using WebApplication receiver = await StartAsync(redis, prefix);
        await using WebApplication? sender = redisBackplane ? await StartAsync(redis, prefix) : null;
        IServiceProvider services = (sender ?? receiver).Services;
        ITicketNotifier notifier = services.GetRequiredService<ITicketNotifier>();
        Guid sessionId = Guid.NewGuid();
        Guid otherSessionId = Guid.NewGuid();
        Guid seatId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Channel<HubEvent> firstEvents = Channel.CreateUnbounded<HubEvent>();
        Channel<HubEvent> otherEvents = Channel.CreateUnbounded<HubEvent>();
        await using HubConnection first = CreateClient(receiver, "/tickets", userId, firstEvents);
        await using HubConnection other = CreateClient(receiver, "/hubs/tickets", otherUserId, otherEvents);
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ConnectionMultiplexer? connection = redisBackplane ? await ConnectionMultiplexer.ConnectAsync(redis!) : null;
        RedisKey lockKey = $"lock:session:{{{sessionId}}}:{seatId}";
        RedisKey setKey = $"locked_seats:session:{{{sessionId}}}";

        try
        {
            await first.StartAsync(ct);
            await other.StartAsync(ct);
            await first.InvokeAsync("JoinSessionGroup", $"session:{sessionId}", ct);
            await other.InvokeAsync("JoinSessionGroup", $"session:{otherSessionId}", ct);

            if (connection is not null)
            {
                RedisSeatLockingService locks = new(connection, notifier, NullLogger<RedisSeatLockingService>.Instance);
                (await locks.LockSeatAsync(sessionId, seatId, userId, ct)).IsSuccess.Should().BeTrue();
                (await locks.LockSeatAsync(sessionId, seatId, userId, ct)).IsSuccess.Should().BeTrue();
                (await locks.LockSeatAsync(sessionId, seatId, otherUserId, ct)).IsFailure.Should().BeTrue();
                (await locks.UnlockSeatAsync(sessionId, seatId, otherUserId, ct)).IsSuccess.Should().BeTrue();
                (await locks.UnlockSeatAsync(sessionId, seatId, userId, ct)).IsSuccess.Should().BeTrue();
            }
            else
            {
                await notifier.NotifySeatLockedAsync(sessionId, seatId, userId, ct);
                await notifier.NotifySeatUnlockedAsync(sessionId, seatId, ct);
            }

            await notifier.NotifySeatLockedAsync(otherSessionId, seatId, otherUserId, ct);
            Guid orderId = Guid.NewGuid();
            await notifier.NotifyOrderFailed(userId, orderId, "Payment declined");

            List<HubEvent> firstBatch = await FlushAsync(notifier, userId, firstEvents.Reader);
            List<HubEvent> otherBatch = await FlushAsync(notifier, otherUserId, otherEvents.Reader);
            firstBatch.Select(e => e.Name).Should().Equal("SeatLocked", "SeatUnlocked", "OrderFailed");
            AssertGuids(firstBatch[0], sessionId, seatId, userId);
            AssertGuids(firstBatch[1], sessionId, seatId);
            firstBatch[2].Arguments.Should().HaveCount(1);
            firstBatch[2].Arguments[0].GetProperty("orderId").GetGuid().Should().Be(orderId);
            firstBatch[2].Arguments[0].GetProperty("reason").GetString().Should().Be("Payment declined");
            otherBatch.Should().ContainSingle().Which.Name.Should().Be("SeatLocked");
            AssertGuids(otherBatch[0], otherSessionId, seatId, otherUserId);

            // A fresh connection must rejoin using the same invocation as the frontend's reconnect handler.
            string? previousConnectionId = first.ConnectionId;
            await first.StopAsync(ct);
            await first.StartAsync(ct);
            first.ConnectionId.Should().NotBe(previousConnectionId);
            await first.InvokeAsync("JoinSessionGroup", $"session:{sessionId}", ct);
            await notifier.NotifySeatLockedAsync(sessionId, seatId, userId, ct);
            List<HubEvent> rejoined = await FlushAsync(notifier, userId, firstEvents.Reader);
            rejoined.Should().ContainSingle().Which.Name.Should().Be("SeatLocked");
            AssertGuids(rejoined[0], sessionId, seatId, userId);

            await first.InvokeAsync("LeaveSessionGroup", $"session:{sessionId}", ct);
            await notifier.NotifySeatUnlockedAsync(sessionId, seatId, ct);
            (await FlushAsync(notifier, userId, firstEvents.Reader)).Should().BeEmpty();
            (await FlushAsync(notifier, otherUserId, otherEvents.Reader)).Should().BeEmpty();
        }
        finally
        {
            if (connection is not null)
                await connection.GetDatabase().KeyDeleteAsync([lockKey, setKey]);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            if (sender is not null)
                await sender.StopAsync(timeout.Token);
            await receiver.StopAsync(timeout.Token);
        }
    }

    private static async Task<WebApplication> StartAsync(string? redis, string prefix)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var signalR = builder.Services.AddSignalR();
        if (redis is not null)
            signalR.AddStackExchangeRedis(redis, options => options.Configuration.ChannelPrefix = RedisChannel.Literal(prefix));
        builder.Services.AddSingleton<TicketNotificationWorker>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<TicketNotificationWorker>());
        builder.Services.AddSingleton<ITicketNotifier>(provider => provider.GetRequiredService<TicketNotificationWorker>());
        WebApplication app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, context.Request.Headers["X-Test-User"].ToString())], "Test"));
            return next(context);
        });
        app.MapHub<TicketHub>("/tickets");
        app.MapInfrastructureEndpoints();
        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static HubConnection CreateClient(WebApplication app, string path, Guid userId, Channel<HubEvent> events)
    {
        HubConnection connection = new HubConnectionBuilder().WithUrl($"http://localhost{path}", options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
            options.Headers["X-Test-User"] = userId.ToString();
        }).Build();
        Register("SeatLocked", 3);
        Register("SeatUnlocked", 2);
        Register("ReceiveSeatStatusChange", 3);
        Register("OrderCompleted", 1);
        Register("OrderFailed", 1);
        return connection;

        void Register(string name, int arguments)
        {
            connection.On(name, Enumerable.Repeat(typeof(JsonElement), arguments).ToArray(), (args, _) =>
            {
                events.Writer.TryWrite(new HubEvent(name, args.Cast<JsonElement>().ToArray()));
                return Task.CompletedTask;
            }, events);
        }
    }

    private static async Task<List<HubEvent>> FlushAsync(ITicketNotifier notifier, Guid userId, ChannelReader<HubEvent> events)
    {
        // A user-targeted marker follows all queued events on this connection, avoiding timing-based absence assertions.
        Guid marker = Guid.NewGuid();
        await notifier.NotifyOrderCompleted(userId, marker);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        List<HubEvent> received = [];
        while (true)
        {
            HubEvent current = await events.ReadAsync(timeout.Token);
            if (current.Name == "OrderCompleted" && current.Arguments[0].GetGuid() == marker)
                return received;
            received.Add(current);
        }
    }

    private static void AssertGuids(HubEvent notification, params Guid[] expected)
    {
        notification.Arguments.Select(argument => argument.ValueKind).Should().OnlyContain(kind => kind == JsonValueKind.String);
        notification.Arguments.Select(argument => argument.GetGuid()).Should().Equal(expected);
    }

    private sealed record HubEvent(string Name, JsonElement[] Arguments);
}
