using System.Text.Json;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Sessions.Queries.GetSessionSeats;
using Cinema.Booking.Infrastructure.Services;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;
using Order = Cinema.Domain.Entities.Order;

namespace Cinema.BookingTests;

public class SessionSeatAvailabilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Availability_Preserves_Sparse_Seats_And_Persisted_Occupancy_Over_Redis(bool authenticated)
    {
        await using ApplicationDbContext context = new AvailabilityDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Hall hall = Hall.Create(EntityId<Hall>.New(), "Sparse hall");
        SeatType type = SeatType.New(EntityId<SeatType>.New(), "Standard", null);
        Movie movie = Movie.CreateManual("Test movie", "Test", 90, DateTime.UtcNow.Year, MovieStatus.ComingSoon);
        Pricing pricing = Pricing.New(EntityId<Pricing>.New(), "Test pricing");
        DateTime start = DateTime.UtcNow.AddDays(1);
        Session session = Session.Create(EntityId<Session>.New(), start, start.AddHours(2), movie.Id, hall.Id, pricing.Id);
        Seat[] seats = Enumerable.Range(0, 8).Select(index => Seat.New(EntityId<Seat>.New(),
            $"Row {index + 1}", index + 10, index * 2 + 3, index + 2, SeatStatus.Active, hall.Id, type.Id)).ToArray();
        hall.ApplyLayout(seats);
        seats[7].SetStatus(SeatStatus.Maintenance);
        context.Halls.Add(hall);
        context.SeatTypes.Add(type);
        context.Movies.Add(movie);
        context.Pricings.Add(pricing);
        context.Sessions.Add(session);
        TicketStatus[] persisted = [TicketStatus.Reserved, TicketStatus.Valid, TicketStatus.Used];
        for (int index = 0; index < persisted.Length; index++)
        {
            context.Tickets.Add(Ticket.New(EntityId<Ticket>.New(), 100m, persisted[index],
                EntityId<Order>.New(), session.Id, seats[index + 4].Id));
        }
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();

        Guid userId = Guid.NewGuid();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(authenticated ? userId : null);
        IConnectionMultiplexer connection = Substitute.For<IConnectionMultiplexer>();
        IDatabase database = Substitute.For<IDatabase>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        // Even a stale own Redis lock cannot override a persisted ticket.
        Dictionary<string, RedisValue> values = seats.Skip(4).Take(3)
            .ToDictionary(seat => Key(seat), _ => (RedisValue)userId.ToString());
        values[Key(seats[1])] = userId.ToString();
        values[Key(seats[2])] = Guid.NewGuid().ToString();
        database.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(call => ((RedisKey[])call[0]).Select(key => values.GetValueOrDefault(key.ToString(), RedisValue.Null)).ToArray());
        RedisSeatLockingService locks = new(connection, Substitute.For<ITicketNotifier>(),
            NullLogger<RedisSeatLockingService>.Instance);
        IPriceCalculator prices = Substitute.For<IPriceCalculator>();
        prices.CalculatePrice(Arg.Any<Pricing>(), type.Id, start).Returns(125m);
        ISeatTypeProvider types = Substitute.For<ISeatTypeProvider>();
        types.GetName(type.Id).Returns("Standard");
        GetSessionSeatsQueryHandler handler = new(context, prices, locks, types, currentUser);

        var result = await handler.Handle(new GetSessionSeatsQuery(session.Id.Value), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.SessionId.Should().Be(session.Id.Value);
        result.Value.HallId.Should().Be(hall.Id.Value);
        result.Value.MovieTitle.Should().Be(movie.Title);
        result.Value.StartTime.Should().Be(start);
        result.Value.Seats.Should().HaveCount(7);
        for (int index = 0; index < 7; index++)
        {
            Seat seat = seats[index];
            SeatDto actual = result.Value.Seats.Single(dto => dto.Id == seat.Id.Value);
            actual.Row.Should().Be(seat.GridX);
            actual.Number.Should().Be(seat.Number);
            actual.RowLabel.Should().Be(seat.RowLabel);
            actual.GridX.Should().Be(seat.GridX);
            actual.GridY.Should().Be(seat.GridY);
            actual.Type.Should().Be("Standard");
            actual.Price.Should().Be(125m);
            actual.IsSold.Should().Be(index >= 4);
            actual.IsLocked.Should().Be(index is 1 or 2);
            actual.IsAvailable.Should().Be(index is 0 or 3);
            actual.IsLockedByCurrentUser.Should().Be(authenticated && index == 1);
        }
        string[] expectedKeys = seats.Take(4).Select(Key).ToArray();
        await database.Received(1).StringGetAsync(Arg.Is<RedisKey[]>(keys => keys.Select(key => key.ToString())
            .SequenceEqual(expectedKeys)), Arg.Any<CommandFlags>());
        database.ReceivedCalls().Should().ContainSingle();
        prices.Received(1).CalculatePrice(Arg.Any<Pricing>(), type.Id, start);

        string Key(Seat seat) => $"lock:session:{{{session.Id.Value}}}:{seat.Id.Value}";
    }

    [Fact]
    public void Seat_Json_Adds_Only_Ownership_And_Preserves_Legacy_Construction_And_Deserialization()
    {
        SeatDto legacy = new(Guid.NewGuid(), 3, 10, "B", 3, 2, "Standard", 125m, true, false, false);
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        JsonElement json = JsonSerializer.SerializeToElement(legacy, options);
        json.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "id", "row", "number", "rowLabel", "gridX", "gridY", "type", "price",
            "isAvailable", "isSold", "isLocked", "isLockedByCurrentUser");
        json.GetProperty("isLockedByCurrentUser").GetBoolean().Should().BeFalse();
        Dictionary<string, JsonElement> oldPayload = json.EnumerateObject()
            .Where(property => property.Name != "isLockedByCurrentUser")
            .ToDictionary(property => property.Name, property => property.Value);

        SeatDto? decoded = JsonSerializer.Deserialize<SeatDto>(JsonSerializer.Serialize(oldPayload), options);

        decoded.Should().Be(legacy);
        decoded!.IsLockedByCurrentUser.Should().BeFalse();
    }

    private sealed class AvailabilityDbContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Movie>().Ignore(movie => movie.Embedding);
        }
    }
}
