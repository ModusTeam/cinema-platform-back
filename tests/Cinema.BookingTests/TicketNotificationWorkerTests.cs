using Cinema.Api.Hubs;
using Cinema.Api.Modules;
using Cinema.Api.Services;
using Cinema.Application.Common.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Cinema.BookingTests;

public class TicketNotificationWorkerTests
{
    [Fact]
    public async Task Queued_Seat_Events_Use_Typed_Contract_And_Canonical_Group()
    {
        IHubContext<TicketHub, ITicketClient> hub = Substitute.For<IHubContext<TicketHub, ITicketClient>>();
        ITicketClient client = Substitute.For<ITicketClient>();
        Guid sessionId = Guid.NewGuid();
        Guid seatId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        string group = $"session:{sessionId}";
        hub.Clients.Group(group).Returns(client);
        using TicketNotificationWorker worker = new(hub, NullLogger<TicketNotificationWorker>.Instance);

        await worker.NotifySeatLockedAsync(sessionId, seatId, userId, TestContext.Current.CancellationToken);
        await worker.NotifySeatUnlockedAsync(sessionId, seatId, TestContext.Current.CancellationToken);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await StopAsync(worker);

        await client.Received(1).SeatLocked(sessionId, seatId, userId);
        await client.Received(1).SeatUnlocked(sessionId, seatId);
        await client.DidNotReceiveWithAnyArgs().ReceiveSeatStatusChange(default, default!, default);
        hub.Clients.Received(2).Group(group);
        hub.Clients.DidNotReceive().Group(sessionId.ToString());
        worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Stop_Drains_Accepted_Order_Notifications()
    {
        IHubContext<TicketHub, ITicketClient> hub = Substitute.For<IHubContext<TicketHub, ITicketClient>>();
        ITicketClient client = Substitute.For<ITicketClient>();
        Guid userId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        hub.Clients.User(userId.ToString()).Returns(client);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OrderCompleted(orderId).Returns(_ =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        using TicketNotificationWorker worker = new(hub, NullLogger<TicketNotificationWorker>.Instance);
        await worker.NotifyOrderCompleted(userId, orderId);
        await worker.NotifyOrderFailed(userId, orderId, "Payment declined");
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task stop = worker.StopAsync(timeout.Token);
        try
        {
            stop.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
            await stop.WaitAsync(timeout.Token);
        }

        await client.Received(1).OrderCompleted(orderId);
        object error = client.ReceivedCalls().Single(call => call.GetMethodInfo().Name == nameof(ITicketClient.OrderFailed))
            .GetArguments()[0]!;
        error.Should().BeEquivalentTo(new { OrderId = orderId, Reason = "Payment declined" });
        hub.Clients.Received(2).User(userId.ToString());
        worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Stop_Respects_Shutdown_Deadline_When_Dispatch_Is_Blocked()
    {
        IHubContext<TicketHub, ITicketClient> hub = Substitute.For<IHubContext<TicketHub, ITicketClient>>();
        ITicketClient client = Substitute.For<ITicketClient>();
        Guid userId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        hub.Clients.User(userId.ToString()).Returns(client);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OrderCompleted(orderId).Returns(_ =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        using TicketNotificationWorker worker = new(hub, NullLogger<TicketNotificationWorker>.Instance);
        await worker.NotifyOrderCompleted(userId, orderId);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using CancellationTokenSource deadline = new();

        try
        {
            Task stop = worker.StopAsync(deadline.Token);
            stop.IsCompleted.Should().BeFalse();
            deadline.Cancel();
            await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
            await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Failed_Dispatch_Is_Logged_And_Next_Notification_Is_Sent()
    {
        IHubContext<TicketHub, ITicketClient> hub = Substitute.For<IHubContext<TicketHub, ITicketClient>>();
        ITicketClient client = Substitute.For<ITicketClient>();
        ILogger<TicketNotificationWorker> logger = Substitute.For<ILogger<TicketNotificationWorker>>();
        Guid sessionId = Guid.NewGuid();
        Guid seatId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        InvalidOperationException failure = new("Transport failed");
        hub.Clients.Group($"session:{sessionId}").Returns(client);
        client.SeatLocked(sessionId, seatId, userId).Returns(Task.FromException(failure));
        TaskCompletionSource sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SeatUnlocked(sessionId, seatId).Returns(_ =>
        {
            sent.TrySetResult();
            return Task.CompletedTask;
        });
        using TicketNotificationWorker worker = new(hub, logger);
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await worker.NotifySeatLockedAsync(sessionId, seatId, userId, TestContext.Current.CancellationToken);
            await worker.NotifySeatUnlockedAsync(sessionId, seatId, TestContext.Current.CancellationToken);
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            await StopAsync(worker);
        }

        await client.Received(1).SeatUnlocked(sessionId, seatId);
        logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
            && Equals(call.GetArguments()[0], LogLevel.Error)
            && ReferenceEquals(call.GetArguments()[3], failure));
        worker.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelled_Enqueue_Does_Not_Dispatch_And_Stop_Closes_Queue()
    {
        IHubContext<TicketHub, ITicketClient> hub = Substitute.For<IHubContext<TicketHub, ITicketClient>>();
        using TicketNotificationWorker worker = new(hub, NullLogger<TicketNotificationWorker>.Instance);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Func<Task> enqueue = () => worker.NotifySeatLockedAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), cancelled.Token);
        await enqueue.Should().ThrowAsync<OperationCanceledException>();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await StopAsync(worker);

        hub.Clients.DidNotReceiveWithAnyArgs().Group(default!);
        Func<Task> enqueueAfterStop = () => worker.NotifyOrderCompleted(Guid.NewGuid(), Guid.NewGuid());
        await enqueueAfterStop.Should().ThrowAsync<System.Threading.Channels.ChannelClosedException>();
    }

    [Fact]
    public async Task Web_DI_Uses_One_Singleton_For_Worker_Host_And_Notifier()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=cinema_orders_test_registration;Username=test;Password=test",
            ["ConnectionStrings:RedisConnection"] = "localhost:6379"
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddWebServices(configuration);
        services.AddSingleton(Substitute.For<IHubContext<TicketHub, ITicketClient>>());
        await using ServiceProvider provider = services.BuildServiceProvider();

        TicketNotificationWorker worker = provider.GetRequiredService<TicketNotificationWorker>();
        provider.GetRequiredService<ITicketNotifier>().Should().BeSameAs(worker);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITicketNotifier>().Should().BeSameAs(worker);
        services.Single(descriptor => descriptor.ServiceType == typeof(ITicketNotifier)).Lifetime.Should().Be(ServiceLifetime.Singleton);
        services.Single(descriptor => descriptor.ServiceType == typeof(TicketNotificationWorker)).Lifetime.Should().Be(ServiceLifetime.Singleton);
        ServiceDescriptor hosted = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationFactory?.Method.ReturnType == typeof(TicketNotificationWorker));
        hosted.Lifetime.Should().Be(ServiceLifetime.Singleton);
        hosted.ImplementationFactory!(provider).Should().BeSameAs(worker);
        services.Should().NotContain(descriptor => descriptor.ImplementationType == typeof(SignalRTicketNotifier));
    }

    private static async Task StopAsync(TicketNotificationWorker worker)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await worker.StopAsync(timeout.Token).WaitAsync(timeout.Token);
    }
}
