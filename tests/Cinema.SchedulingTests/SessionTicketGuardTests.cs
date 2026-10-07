using Cinema.Application.Services;
using Cinema.Application.Sessions.Commands.CancelSession;
using Cinema.Application.Sessions.Commands.RescheduleSession;
using Cinema.Catalog.Domain.Entities;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Interfaces;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Cinema.SchedulingTests;

public class SessionTicketGuardTests
{
    [Theory]
    [InlineData(TicketStatus.Valid, true)]
    [InlineData(TicketStatus.Used, true)]
    [InlineData(TicketStatus.Refunded, false)]
    [InlineData(null, false)]
    public async Task CancelSession_Rejects_Only_Valid_Or_Used_Tickets(TicketStatus? ticketStatus, bool shouldReject)
    {
        await using ApplicationDbContext context = CreateContext();
        Session session = await SeedSessionAsync(context, ticketStatus);

        var result = await new CancelSessionCommandHandler(context)
            .Handle(new CancelSessionCommand(session.Id.Value), CancellationToken.None);

        result.IsFailure.Should().Be(shouldReject);
        if (shouldReject)
        {
            result.Error.Code.Should().Be("Session.CannotCancel");
            session.Status.Should().Be(SessionStatus.Scheduled);
        }
        else
        {
            session.Status.Should().Be(SessionStatus.Cancelled);
        }
    }

    [Theory]
    [InlineData(TicketStatus.Valid, true)]
    [InlineData(TicketStatus.Used, true)]
    [InlineData(TicketStatus.Refunded, false)]
    [InlineData(null, false)]
    public async Task RescheduleSession_Rejects_Only_Valid_Or_Used_Tickets(TicketStatus? ticketStatus, bool shouldReject)
    {
        await using ApplicationDbContext context = CreateContext();
        Session session = await SeedSessionAsync(context, ticketStatus);
        DateTime originalStart = session.StartTime;
        DateTime newStart = originalStart.AddHours(2);
        IMovieInfoProvider movieProvider = Substitute.For<IMovieInfoProvider>();
        SessionSchedulingService service = new(context, movieProvider);

        var result = await new RescheduleSessionCommandHandler(service, context)
            .Handle(new RescheduleSessionCommand(session.Id.Value, newStart), CancellationToken.None);

        result.IsFailure.Should().Be(shouldReject);
        if (shouldReject)
        {
            result.Error.Code.Should().Be("Session.CannotReschedule");
            session.StartTime.Should().Be(originalStart);
        }
        else
        {
            session.StartTime.Should().Be(newStart);
        }
    }

    private static ApplicationDbContext CreateContext()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SchedulingGuardDbContext(options);
    }

    private static async Task<Session> SeedSessionAsync(ApplicationDbContext context, TicketStatus? ticketStatus)
    {
        EntityId<Hall> hallId = EntityId<Hall>.New();
        Hall hall = Hall.Create(hallId, "Test hall");
        EntityId<SeatType> seatTypeId = EntityId<SeatType>.New();
        SeatType seatType = SeatType.New(seatTypeId, "Standard", null);
        Seat seat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active, hallId, seatTypeId);
        hall.ApplyLayout([seat]);
        DateTime start = DateTime.UtcNow.AddDays(1);
        Session session = Session.Create(EntityId<Session>.New(), start, start.AddHours(2),
            new EntityId<Movie>(Guid.NewGuid()), hallId, EntityId<Pricing>.New());

        context.Halls.Add(hall);
        context.SeatTypes.Add(seatType);
        context.Sessions.Add(session);
        if (ticketStatus.HasValue)
        {
            Ticket ticket = Ticket.New(EntityId<Ticket>.New(), 10m, ticketStatus.Value,
                EntityId<Order>.New(), session.Id, seat.Id);
            context.Tickets.Add(ticket);
        }

        await context.SaveChangesAsync(CancellationToken.None);
        return session;
    }

    private sealed class SchedulingGuardDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Movie>().Ignore(movie => movie.Embedding);
        }
    }
}
