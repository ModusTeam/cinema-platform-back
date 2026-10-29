using Cinema.Application.Services;
using Cinema.Application.Sessions.Commands.RescheduleSession;
using Cinema.Application.Sessions.Dtos;
using Cinema.Catalog.Domain.Entities;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Exceptions;
using Cinema.Domain.Interfaces;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Cinema.SchedulingTests;

public class SessionTimingTests
{
    [Fact]
    public async Task Scheduling_records_movie_end_without_cleaning_time()
    {
        await using ApplicationDbContext context = NewContext();
        EntityId<Movie> movieId = EntityId<Movie>.New();
        IMovieInfoProvider provider = Substitute.For<IMovieInfoProvider>();
        provider.GetDurationMinutesAsync(movieId, Arg.Any<CancellationToken>()).Returns(90);
        DateTime start = DateTime.UtcNow.AddDays(2);

        Session session = await new SessionSchedulingService(context, provider).ScheduleSessionAsync(
            EntityId<Hall>.New(), movieId, EntityId<Pricing>.New(), start, 15, TestContext.Current.CancellationToken);

        session.EndTime.Should().Be(start.AddMinutes(90));
        session.OccupancyEndTime.Should().Be(start.AddMinutes(105));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(180)]
    public void Ordinary_reschedule_rejects_an_original_session_that_has_started(int elapsedMinutes)
    {
        using ApplicationDbContext context = NewContext();
        DateTime future = DateTime.UtcNow.AddDays(2);
        Session session = Session.Create(EntityId<Session>.New(), future, future.AddHours(2),
            EntityId<Movie>.New(), EntityId<Hall>.New(), EntityId<Pricing>.New());
        DateTime originalStart = DateTime.UtcNow.AddMinutes(-elapsedMinutes);
        context.Entry(session).Property(s => s.StartTime).CurrentValue = originalStart;

        Action reschedule = () => session.Reschedule(future.AddDays(1), future.AddDays(1).AddHours(2));

        reschedule.Should().Throw<DomainException>();
        session.StartTime.Should().Be(originalStart);
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(104, true)]
    [InlineData(105, false)]
    public async Task Overlap_uses_occupancy_including_cleaning_and_allows_exact_boundary(int offset, bool overlap)
    {
        await using ApplicationDbContext context = NewContext();
        DateTime start = DateTime.UtcNow.AddDays(2);
        Hall hall = Hall.Create(EntityId<Hall>.New(), "Timing");
        EntityId<Movie> movie = EntityId<Movie>.New();
        EntityId<Pricing> pricing = EntityId<Pricing>.New();
        context.Halls.Add(hall);
        context.Sessions.Add(Session.Create(EntityId<Session>.New(), start, start.AddMinutes(90), movie,
            hall.Id, pricing, occupancyEndTime: start.AddMinutes(105)));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        IMovieInfoProvider provider = Substitute.For<IMovieInfoProvider>();
        provider.GetDurationMinutesAsync(movie, Arg.Any<CancellationToken>()).Returns(90);
        Func<Task> schedule = async () => await new SessionSchedulingService(context, provider).ScheduleSessionAsync(
            hall.Id, movie, pricing, start.AddMinutes(offset), 15, TestContext.Current.CancellationToken);

        if (overlap) await schedule.Should().ThrowAsync<DomainException>().WithMessage("*overlaps*");
        else await schedule.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reschedule_preserves_both_durations_and_unknown_legacy_screening_end(bool legacy)
    {
        await using ApplicationDbContext context = NewContext();
        DateTime start = DateTime.UtcNow.AddDays(2);
        Session session = Session.Create(EntityId<Session>.New(), start, start.AddMinutes(90),
            EntityId<Movie>.New(), EntityId<Hall>.New(), EntityId<Pricing>.New(),
            occupancyEndTime: start.AddMinutes(105));
        if (legacy) context.Entry(session).Property(s => s.EndTime).CurrentValue = null;
        IMovieInfoProvider provider = Substitute.For<IMovieInfoProvider>();

        await new SessionSchedulingService(context, provider).RescheduleSessionAsync(session, start.AddDays(1),
            TestContext.Current.CancellationToken);

        session.EndTime.Should().Be(legacy ? null : start.AddDays(1).AddMinutes(90));
        session.OccupancyEndTime.Should().Be(start.AddDays(1).AddMinutes(105));
        provider.ReceivedCalls().Should().BeEmpty();
        SessionDto dto = SessionDto.FromDomainModel(session);
        dto.EndTime.Should().Be(session.EndTime);
        dto.OccupancyEndTime.Should().Be(session.OccupancyEndTime);
    }

    [Fact]
    public async Task Reschedule_handler_returns_a_defined_failure_for_started_session()
    {
        await using ApplicationDbContext context = NewContext();
        DateTime future = DateTime.UtcNow.AddDays(2);
        Hall hall = Hall.Create(EntityId<Hall>.New(), "Timing");
        Session session = Session.Create(EntityId<Session>.New(), future, future.AddHours(2),
            EntityId<Movie>.New(), hall.Id, EntityId<Pricing>.New());
        context.Halls.Add(hall);
        context.Sessions.Add(session);
        DateTime originalStart = DateTime.UtcNow.AddMinutes(-1);
        context.Entry(session).Property(s => s.StartTime).CurrentValue = originalStart;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var result = await new RescheduleSessionCommandHandler(
            new SessionSchedulingService(context, Substitute.For<IMovieInfoProvider>()), context)
            .Handle(new(session.Id.Value, future), TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("Session.CannotReschedule");
        session.StartTime.Should().Be(originalStart);
    }

    [Fact]
    public void Cancellation_retains_the_existing_cleaning_window_guard()
    {
        DateTime start = DateTime.UtcNow.AddDays(2);
        Session session = Session.Create(EntityId<Session>.New(), start, start.AddMinutes(90),
            EntityId<Movie>.New(), EntityId<Hall>.New(), EntityId<Pricing>.New(),
            occupancyEndTime: start.AddMinutes(105));
        Action cancel = () => session.Cancel(start.AddMinutes(95));
        cancel.Should().Throw<DomainException>();
    }

    private static ApplicationDbContext NewContext() => new TimingContext(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class TimingContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Movie>().Ignore(movie => movie.Embedding);
        }
    }
}
