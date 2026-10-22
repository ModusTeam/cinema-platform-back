using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Booking;
using Cinema.Booking.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Cinema.BookingTests;

public class SeatLockOwnershipTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Batched_Read_Reports_Only_Live_Locks_And_Current_User_Ownership(bool authenticated)
    {
        IConnectionMultiplexer connection = Substitute.For<IConnectionMultiplexer>();
        IDatabase database = Substitute.For<IDatabase>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        Guid sessionId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Guid[] seats = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns([userId.ToString(), Guid.NewGuid().ToString(), RedisValue.Null, "invalid-owner"]);
        RedisSeatLockingService service = new(connection, Substitute.For<ITicketNotifier>(),
            NullLogger<RedisSeatLockingService>.Instance);

        IReadOnlyList<SeatLockState> locks = await service.GetSeatLocksAsync(sessionId, seats,
            authenticated ? userId : null, TestContext.Current.CancellationToken);

        locks.Should().Equal(new SeatLockState(seats[0], authenticated),
            new SeatLockState(seats[1], false), new SeatLockState(seats[3], false));
        await database.Received(1).StringGetAsync(Arg.Is<RedisKey[]>(keys =>
            keys.Select(key => key.ToString()).SequenceEqual(seats.Select(seat => $"lock:session:{{{sessionId}}}:{seat}"))),
            Arg.Any<CommandFlags>());
        database.ReceivedCalls().Should().ContainSingle();
    }

    [Fact]
    public async Task Empty_Seat_List_Does_Not_Query_Redis()
    {
        IConnectionMultiplexer connection = Substitute.For<IConnectionMultiplexer>();
        IDatabase database = Substitute.For<IDatabase>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        RedisSeatLockingService service = new(connection, Substitute.For<ITicketNotifier>(),
            NullLogger<RedisSeatLockingService>.Instance);

        (await service.GetSeatLocksAsync(Guid.NewGuid(), [], Guid.NewGuid(), TestContext.Current.CancellationToken))
            .Should().BeEmpty();

        database.ReceivedCalls().Should().BeEmpty();
    }
}
