using Cinema.Catalog.Domain.Entities;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace Cinema.SchedulingTests;

public class SessionCompletionTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(100, true)]
    public void Completion_uses_screening_end_not_worker_or_occupancy(int delay, bool expected)
    {
        Session session = NewSession();
        session.TryComplete(session.EndTime!.Value.AddSeconds(delay)).Should().Be(expected);
        session.Status.Should().Be(expected ? SessionStatus.Ended : SessionStatus.Scheduled);
        if (expected) session.TryComplete(session.OccupancyEndTime.AddDays(1)).Should().BeFalse();
    }

    [Fact]
    public void Cancelled_session_never_completes()
    {
        Session session = NewSession();
        session.Cancel(session.StartTime.AddMinutes(-1));
        session.TryComplete(session.EndTime!.Value).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(240)]
    public void Ordinary_cancellation_rejects_start_and_after_occupancy(int minutes)
    {
        Session session = NewSession();
        Action cancel = () => session.Cancel(session.StartTime.AddMinutes(minutes));
        cancel.Should().Throw<DomainException>();
        session.Status.Should().Be(SessionStatus.Scheduled);
    }

    private static Session NewSession()
    {
        DateTime start = DateTime.UtcNow.AddDays(2);
        return Session.Create(EntityId<Session>.New(), start, start.AddHours(2),
            EntityId<Movie>.New(), EntityId<Hall>.New(), EntityId<Pricing>.New(),
            occupancyEndTime: start.AddHours(2).AddMinutes(15));
    }
}
