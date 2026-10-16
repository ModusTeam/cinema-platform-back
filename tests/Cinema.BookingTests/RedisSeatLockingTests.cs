using Cinema.Application.Common.Interfaces;
using Cinema.Booking.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Cinema.BookingTests;

public class RedisSeatLockingTests
{
    [Fact]
    public async Task Failed_Lock_Read_Does_Not_Report_Seats_As_Unlocked()
    {
        IConnectionMultiplexer connection = Substitute.For<IConnectionMultiplexer>();
        IDatabase database = Substitute.For<IDatabase>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(_ => Task.FromException<RedisValue[]>(new InvalidOperationException("Redis unavailable")));
        RedisSeatLockingService service = new(connection,
            Substitute.For<ITicketNotifier>(), NullLogger<RedisSeatLockingService>.Instance);

        Func<Task> read = async () => await service.GetLockedSeatsAsync(Guid.NewGuid(), [Guid.NewGuid()]);

        await read.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Failed_Session_Index_Read_Does_Not_Report_Empty_Occupancy()
    {
        IConnectionMultiplexer connection = Substitute.For<IConnectionMultiplexer>();
        IDatabase database = Substitute.For<IDatabase>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        database.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(_ => Task.FromException<RedisValue[]>(new InvalidOperationException("Redis unavailable")));
        RedisSeatLockingService service = new(connection,
            Substitute.For<ITicketNotifier>(), NullLogger<RedisSeatLockingService>.Instance);

        Func<Task> read = async () => await service.GetLockedSeatsBySessionAsync(Guid.NewGuid());

        await read.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Concurrent_Lock_Attempts_Have_One_Owner()
    {
        string? connectionString = Environment.GetEnvironmentVariable("CINEMA_TEST_REDIS_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Skip("Set CINEMA_TEST_REDIS_CONNECTION to run the isolated Redis integration test.");

        using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        IDatabase database = connection.GetDatabase();
        Guid sessionId = Guid.NewGuid();
        Guid seatId = Guid.NewGuid();
        Guid firstUser = Guid.NewGuid();
        Guid secondUser = Guid.NewGuid();
        RedisKey lockKey = $"lock:session:{{{sessionId}}}:{seatId}";
        RedisKey setKey = $"locked_seats:session:{{{sessionId}}}";
        RedisSeatLockingService service = new(connection,
            Substitute.For<ITicketNotifier>(), NullLogger<RedisSeatLockingService>.Instance);
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken ct = TestContext.Current.CancellationToken;

        try
        {
            Task<Cinema.Domain.Shared.Result> first = Task.Run(async () =>
            {
                await start.Task;
                return await service.LockSeatAsync(sessionId, seatId, firstUser, ct);
            }, ct);
            Task<Cinema.Domain.Shared.Result> second = Task.Run(async () =>
            {
                await start.Task;
                return await service.LockSeatAsync(sessionId, seatId, secondUser, ct);
            }, ct);
            start.SetResult();
            Cinema.Domain.Shared.Result[] results = await Task.WhenAll(first, second);
            results.Count(result => result.IsSuccess).Should().Be(1);
            (await service.GetLockedSeatsBySessionAsync(sessionId, ct)).Should().BeEquivalentTo([seatId]);
            RedisValue owner = await database.StringGetAsync(lockKey);
            owner.ToString().Should().BeOneOf(firstUser.ToString(), secondUser.ToString());
        }
        finally
        {
            await database.KeyDeleteAsync([lockKey, setKey]);
        }
    }

    [Fact]
    public async Task Batch_Unlock_Only_Removes_And_Notifies_Owned_Seats()
    {
        string? connectionString = Environment.GetEnvironmentVariable("CINEMA_TEST_REDIS_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Skip("Set CINEMA_TEST_REDIS_CONNECTION to run the isolated Redis integration test.");

        using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        IDatabase database = connection.GetDatabase();
        Guid sessionId = Guid.NewGuid();
        Guid ownedSeat = Guid.NewGuid();
        Guid foreignSeat = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        RedisKey ownedKey = $"lock:session:{{{sessionId}}}:{ownedSeat}";
        RedisKey foreignKey = $"lock:session:{{{sessionId}}}:{foreignSeat}";
        RedisKey setKey = $"locked_seats:session:{{{sessionId}}}";
        ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
        RedisSeatLockingService service = new(connection, notifier, NullLogger<RedisSeatLockingService>.Instance);
        CancellationToken ct = TestContext.Current.CancellationToken;

        try
        {
            (await service.LockSeatAsync(sessionId, ownedSeat, owner, ct)).IsSuccess.Should().BeTrue();
            (await service.LockSeatAsync(sessionId, foreignSeat, other, ct)).IsSuccess.Should().BeTrue();
            (await service.UnlockSeatsAsync(sessionId, [ownedSeat, foreignSeat], owner, ct)).IsSuccess.Should().BeTrue();

            (await database.KeyExistsAsync(ownedKey)).Should().BeFalse();
            (await database.KeyExistsAsync(foreignKey)).Should().BeTrue();
            (await service.GetLockedSeatsBySessionAsync(sessionId, ct)).Should().BeEquivalentTo([foreignSeat]);
            await notifier.Received(1).NotifySeatUnlockedAsync(sessionId, ownedSeat, Arg.Any<CancellationToken>());
            await notifier.DidNotReceive().NotifySeatUnlockedAsync(sessionId, foreignSeat, Arg.Any<CancellationToken>());
        }
        finally
        {
            await database.KeyDeleteAsync([ownedKey, foreignKey, setKey]);
        }
    }

    [Fact]
    public async Task Locking_Respects_Ownership_Extension_And_Expiry()
    {
        string? connectionString = Environment.GetEnvironmentVariable("CINEMA_TEST_REDIS_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Skip("Set CINEMA_TEST_REDIS_CONNECTION to run the isolated Redis integration test.");

        using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(connectionString!);
        IDatabase database = connection.GetDatabase();
        Guid sessionId = Guid.NewGuid();
        Guid seatId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        Guid otherId = Guid.NewGuid();
        RedisKey lockKey = $"lock:session:{{{sessionId}}}:{seatId}";
        RedisKey setKey = $"locked_seats:session:{{{sessionId}}}";
        RedisSeatLockingService service = new(connection,
            Substitute.For<ITicketNotifier>(), NullLogger<RedisSeatLockingService>.Instance);
        CancellationToken ct = TestContext.Current.CancellationToken;

        try
        {
            (await service.LockSeatAsync(sessionId, seatId, ownerId, ct)).IsSuccess.Should().BeTrue();
            (await database.KeyTimeToLiveAsync(lockKey)).Should().BeCloseTo(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));

            (await service.LockSeatAsync(sessionId, seatId, ownerId, ct)).IsSuccess.Should().BeTrue();
            (await database.KeyTimeToLiveAsync(lockKey)).Should().BeCloseTo(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));

            var conflict = await service.LockSeatAsync(sessionId, seatId, otherId, ct);
            conflict.IsFailure.Should().BeTrue();
            conflict.Error.Code.Should().Be("Seat.AlreadyLocked");

            (await service.ValidateAndExtendLockAsync(sessionId, seatId, ownerId, ct)).Should().BeTrue();
            (await database.KeyTimeToLiveAsync(lockKey)).Should().BeCloseTo(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));

            (await service.UnlockSeatAsync(sessionId, seatId, otherId, ct)).IsSuccess.Should().BeTrue();
            (await service.ValidateAndExtendLockAsync(sessionId, seatId, ownerId, ct)).Should().BeTrue();

            (await service.UnlockSeatAsync(sessionId, seatId, ownerId, ct)).IsSuccess.Should().BeTrue();
            (await database.KeyExistsAsync(lockKey)).Should().BeFalse();

            (await service.LockSeatAsync(sessionId, seatId, ownerId, ct)).IsSuccess.Should().BeTrue();
            await database.KeyExpireAsync(lockKey, TimeSpan.Zero);
            (await service.GetLockedSeatsBySessionAsync(sessionId, ct)).Should().BeEmpty();
            (await service.LockSeatAsync(sessionId, seatId, otherId, ct)).IsSuccess.Should().BeTrue();
            (await service.GetLockedSeatsBySessionAsync(sessionId, ct)).Should().BeEquivalentTo([seatId]);
        }
        finally
        {
            await database.KeyDeleteAsync(lockKey);
            await database.KeyDeleteAsync(setKey);
            (await database.KeyExistsAsync(lockKey)).Should().BeFalse();
            (await database.KeyExistsAsync(setKey)).Should().BeFalse();
        }
    }
}
