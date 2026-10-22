using Cinema.Application.Common.Interfaces;
using Cinema.Booking.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Cinema.BookingTests;

public class RedisSeatNotificationTests
{
    [Theory]
    [InlineData(1, 0, true, 1)]
    [InlineData(0, 1, true, 0)]
    [InlineData(0, 0, false, 0)]
    public async Task Only_New_Locks_Notify(int acquired, int renewed, bool success, int notifications)
    {
        var (service, database, notifier) = CreateService();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)acquired)), Task.FromResult(RedisResult.Create((RedisValue)renewed)));
        Guid sessionId = Guid.NewGuid();
        Guid seatId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        CancellationToken ct = TestContext.Current.CancellationToken;

        (await service.LockSeatAsync(sessionId, seatId, userId, ct)).IsSuccess.Should().Be(success);

        await notifier.Received(notifications).NotifySeatLockedAsync(sessionId, seatId, userId, ct);
        notifier.ReceivedCalls().Should().HaveCount(notifications);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Only_Removed_Owner_Locks_Notify_Unlock(int removed)
    {
        var (service, database, notifier) = CreateService();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)removed)));
        Guid sessionId = Guid.NewGuid();
        Guid seatId = Guid.NewGuid();
        CancellationToken ct = TestContext.Current.CancellationToken;

        (await service.UnlockSeatAsync(sessionId, seatId, Guid.NewGuid(), ct)).IsSuccess.Should().BeTrue();

        await notifier.Received(removed).NotifySeatUnlockedAsync(sessionId, seatId, ct);
        notifier.ReceivedCalls().Should().HaveCount(removed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_Redis_Mutation_Does_Not_Notify(bool locking)
    {
        var (service, database, notifier) = CreateService();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromException<RedisResult>(new InvalidOperationException("Redis unavailable")));
        Guid sessionId = Guid.NewGuid();
        Guid seatId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        CancellationToken ct = TestContext.Current.CancellationToken;

        var result = locking
            ? await service.LockSeatAsync(sessionId, seatId, userId, ct)
            : await service.UnlockSeatAsync(sessionId, seatId, userId, ct);

        result.IsFailure.Should().BeTrue();
        notifier.ReceivedCalls().Should().BeEmpty();
    }

    private static (RedisSeatLockingService Service, IDatabase Database, ITicketNotifier Notifier) CreateService()
    {
        IConnectionMultiplexer connection = Substitute.For<IConnectionMultiplexer>();
        IDatabase database = Substitute.For<IDatabase>();
        ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        return (new RedisSeatLockingService(connection, notifier, NullLogger<RedisSeatLockingService>.Instance), database, notifier);
    }
}
