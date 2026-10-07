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
