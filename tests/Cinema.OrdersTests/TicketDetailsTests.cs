using Cinema.Application;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Orders.Dtos;
using Cinema.Application.Tickets.Queries.GetTicketDetails;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

public class TicketDetailsTests
{
    [Fact]
    public async Task Owner_Receives_All_TicketDto_Fields_From_Persisted_Ticket()
    {
        (string databaseName, Guid ticketId, Guid ownerId, DateTime start) = await SeedAsync();
        new ServiceCollection().AddApplication();
        await using ApplicationDbContext context = CreateContext(databaseName);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(ownerId);

        Result<TicketDto> result = await new GetTicketDetailsQueryHandler(context, currentUser)
            .Handle(new GetTicketDetailsQuery(ticketId), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new TicketDto(ticketId, "Ticket detail movie", "poster.jpg", "Ticket detail hall",
            "B", 4, "Premium", 35m, start, nameof(TicketStatus.Valid),
            ticketId.ToString()[..8].ToUpperInvariant()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Non_owner_Including_Admin_Or_Missing_User_Cannot_Receive_TicketDto(bool isAdmin, bool missingUser)
    {
        (string databaseName, Guid ticketId, _, _) = await SeedAsync();
        await using ApplicationDbContext context = CreateContext(databaseName);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(missingUser ? null : Guid.NewGuid());
        currentUser.IsInRole("Admin").Returns(isAdmin);

        Result<TicketDto> result = await new GetTicketDetailsQueryHandler(context, currentUser)
            .Handle(new GetTicketDetailsQuery(ticketId), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Ticket.AccessDenied");
        Action readValue = () => _ = result.Value;
        readValue.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Missing_Ticket_Returns_NotFound()
    {
        (string databaseName, _, Guid ownerId, _) = await SeedAsync();
        await using ApplicationDbContext context = CreateContext(databaseName);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(ownerId);

        Result<TicketDto> result = await new GetTicketDetailsQueryHandler(context, currentUser)
            .Handle(new GetTicketDetailsQuery(Guid.NewGuid()), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Ticket.NotFound");
    }

    [Fact]
    public async Task Missing_Associated_Order_Returns_AccessDenied()
    {
        (string databaseName, _, Guid ownerId, _) = await SeedAsync();
        Guid orphanId = Guid.NewGuid();
        await using (ApplicationDbContext seed = CreateContext(databaseName))
        {
            Ticket existing = await seed.Tickets.SingleAsync(TestContext.Current.CancellationToken);
            seed.Tickets.Add(Ticket.New(new EntityId<Ticket>(orphanId), 35m, TicketStatus.Valid,
                EntityId<Order>.New(), existing.SessionId, existing.SeatId));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using ApplicationDbContext context = CreateContext(databaseName);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(ownerId);

        Result<TicketDto> result = await new GetTicketDetailsQueryHandler(context, currentUser)
            .Handle(new GetTicketDetailsQuery(orphanId), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Ticket.AccessDenied");
    }

    [Fact]
    public async Task Inactive_Hall_Ticket_Returns_NotFound()
    {
        (string databaseName, Guid ticketId, Guid ownerId, _) = await SeedAsync();
        await using (ApplicationDbContext update = CreateContext(databaseName))
        {
            Hall hall = await update.Halls.SingleAsync(TestContext.Current.CancellationToken);
            hall.Deactivate();
            await update.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using ApplicationDbContext context = CreateContext(databaseName);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(ownerId);

        Result<TicketDto> result = await new GetTicketDetailsQueryHandler(context, currentUser)
            .Handle(new GetTicketDetailsQuery(ticketId), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Ticket.NotFound");
    }

    private static async Task<(string DatabaseName, Guid TicketId, Guid OwnerId, DateTime Start)> SeedAsync()
    {
        string databaseName = Guid.NewGuid().ToString();
        await using ApplicationDbContext context = CreateContext(databaseName);
        Guid ownerId = Guid.NewGuid();
        Hall hall = Hall.Create(EntityId<Hall>.New(), "Ticket detail hall");
        Movie movie = Movie.CreateManual("Ticket detail movie", "test", 90, DateTime.UtcNow.Year,
            MovieStatus.ComingSoon);
        movie.UpdateImages("poster.jpg", null, null);
        SeatType seatType = SeatType.New(EntityId<SeatType>.New(), "Premium", null);
        Seat seat = Seat.New(EntityId<Seat>.New(), "B", 4, 1, 4, SeatStatus.Active, hall.Id, seatType.Id);
        hall.ApplyLayout([seat]);
        DateTime start = DateTime.UtcNow.AddDays(2);
        Session session = Session.Create(EntityId<Session>.New(), start, start.AddHours(2),
            movie.Id, hall.Id, EntityId<Pricing>.New());
        Order order = OrderTicketFixture.CreateAndTrack(context, ownerId, session, [seat], new() { [seat.Id] = 35m });
        context.Halls.Add(hall);
        context.Movies.Add(movie);
        context.SeatTypes.Add(seatType);
        context.Sessions.Add(session);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (databaseName, order.Tickets.Single().Id.Value, ownerId, start);
    }

    private static ApplicationDbContext CreateContext(string databaseName)
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName).Options;
        return new TicketDetailsDbContext(options);
    }

    private sealed class TicketDetailsDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Movie>().Ignore(movie => movie.Embedding);
        }
    }
}
