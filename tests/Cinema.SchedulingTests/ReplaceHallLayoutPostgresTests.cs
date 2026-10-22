using Cinema.Application.Halls.Commands.ReplaceHallLayout;
using Cinema.Application.Seats.Commands.BatchChangeSeatType;
using Cinema.Application.Seats.Commands.UpdateSeat;
using Cinema.Application.Services;
using Cinema.Application.Sessions.Commands.CreateSession;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Interfaces;
using Cinema.Domain.Services;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using Cinema.OrdersTests;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Cinema.SchedulingTests;

public class ReplaceHallLayoutPostgresTests(PostgresSchemaTemplate template) : IClassFixture<PostgresSchemaTemplate>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReplaceHallLayoutCommand Command(HallLayoutDatabase db)
        => new(db.Hall.Id.Value, 5, 8,
            [new(8, 1, db.Standard.Id.Value), new(2, 1, db.Premium.Id.Value), new(3, 4, db.Standard.Id.Value)]);

    private static Task<Result> Replace(ApplicationDbContext context, ReplaceHallLayoutCommand command)
        => new ReplaceHallLayoutCommandHandler(context, new SeatLayoutService(), new HallLayoutEditingService(context))
            .Handle(command, Ct);

    [Fact]
    public async Task Replacement_persists_only_new_seats_with_types_gaps_numbering_and_capacity()
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        await using ApplicationDbContext context = db.NewContext();
        EntityId<Seat>[] originalIds = db.Hall.Seats.Select(s => s.Id).ToArray();

        (await Replace(context, Command(db))).IsSuccess.Should().BeTrue();

        await using ApplicationDbContext verify = db.NewContext();
        Hall hall = await verify.Halls.Include(h => h.Seats).SingleAsync(Ct);
        hall.TotalCapacity.Should().Be(3);
        hall.Name.Should().Be(db.Hall.Name);
        hall.Seats.Should().HaveCount(3);
        (await verify.Seats.IgnoreQueryFilters().CountAsync(Ct)).Should().Be(3);
        hall.Seats.Should().OnlyContain(s => !originalIds.Contains(s.Id) && s.HallId == hall.Id);
        hall.Seats.Select(s => (s.GridX, s.GridY, s.RowLabel, s.Number, s.SeatTypeId))
            .Should().BeEquivalentTo(new[]
            {
                (2, 1, "1", 1, db.Premium.Id), (8, 1, "1", 2, db.Standard.Id),
                (3, 4, "4", 1, db.Standard.Id)
            });
    }

    [Theory]
    [InlineData("future")]
    [InlineData("past")]
    [InlineData("current")]
    [InlineData("cancelled")]
    public async Task Every_persisted_session_blocks_replacement_and_preserves_original_seats(string history)
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        await using ApplicationDbContext context = db.NewContext();
        Session session = db.NewSession();
        if (history == "cancelled") session.Cancel(DateTime.UtcNow);
        context.Sessions.Add(session);
        await context.SaveChangesAsync(Ct);
        if (history is "past" or "current")
        {
            context.Entry(session).Property(s => s.StartTime).CurrentValue = DateTime.UtcNow.AddHours(-2);
            context.Entry(session).Property(s => s.EndTime).CurrentValue =
                history == "past" ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddHours(1);
            await context.SaveChangesAsync(Ct);
        }

        Result result = await Replace(context, Command(db));

        result.Error.Code.Should().Be("Hall.LayoutLocked");
        await AssertOriginal(db);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_inactive_hall_is_rejected(bool missing)
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        await using ApplicationDbContext context = db.NewContext();
        var command = Command(db);
        if (missing) command = command with { HallId = Guid.NewGuid() };
        else
        {
            Hall hall = await context.Halls.SingleAsync(Ct);
            hall.Deactivate();
            context.Sessions.Add(db.NewSession());
            await context.SaveChangesAsync(Ct);
            (await context.Sessions.CountAsync(Ct)).Should().Be(0);
            (await context.Sessions.IgnoreQueryFilters().CountAsync(Ct)).Should().Be(1);
        }

        (await Replace(context, command)).Error.Code.Should().Be(missing ? "Hall.NotFound" : "Hall.Inactive");
        await AssertOriginal(db);
    }

    [Fact]
    public async Task Unknown_seat_type_does_not_change_original_layout()
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        await using ApplicationDbContext context = db.NewContext();
        var command = Command(db) with { Seats = [new(1, 1, Guid.NewGuid())] };

        (await Replace(context, command)).Error.Code.Should().Be("SeatType.NotFound");
        await AssertOriginal(db);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Single_and_batch_type_changes_share_history_policy(bool single, bool hasHistory)
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        await using ApplicationDbContext context = db.NewContext();
        if (hasHistory)
        {
            Session session = db.NewSession();
            session.Cancel(DateTime.UtcNow);
            context.Sessions.Add(session);
            await context.SaveChangesAsync(Ct);
        }
        Guid seatId = db.Hall.Seats.First().Id.Value;
        HallLayoutEditingService service = new(context);
        Result result = single
            ? await new ChangeSeatTypeHandler(context, service).Handle(new(seatId, db.Premium.Id.Value), Ct)
            : await new BatchChangeSeatTypeCommandHandler(context, service)
                .Handle(new(db.Hall.Id.Value, [seatId], db.Premium.Id.Value), Ct);

        if (hasHistory)
        {
            result.Error.Code.Should().Be("Hall.LayoutLocked");
            await AssertOriginal(db);
        }
        else
        {
            result.IsSuccess.Should().BeTrue();
            await using ApplicationDbContext verify = db.NewContext();
            (await verify.Seats.SingleAsync(s => s.Id == new EntityId<Seat>(seatId), Ct))
                .SeatTypeId.Should().Be(db.Premium.Id);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Type_change_reports_unknown_type_and_missing_seats(bool single)
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        await using ApplicationDbContext context = db.NewContext();
        HallLayoutEditingService service = new(context);
        Guid seatId = db.Hall.Seats.First().Id.Value;
        Result unknown = single
            ? await new ChangeSeatTypeHandler(context, service).Handle(new(seatId, Guid.NewGuid()), Ct)
            : await new BatchChangeSeatTypeCommandHandler(context, service)
                .Handle(new(db.Hall.Id.Value, [seatId], Guid.NewGuid()), Ct);
        unknown.Error.Code.Should().Be("SeatType.NotFound");
        Result missing = single
            ? await new ChangeSeatTypeHandler(context, service).Handle(new(Guid.NewGuid(), db.Standard.Id.Value), Ct)
            : await new BatchChangeSeatTypeCommandHandler(context, service)
                .Handle(new(db.Hall.Id.Value, [Guid.NewGuid()], db.Standard.Id.Value), Ct);
        missing.Error.Code.Should().Be(single ? "Seat.NotFound" : "Seats.NotFound");
        await AssertOriginal(db);
    }

    [Fact]
    public async Task Seat_linked_record_in_another_halls_session_also_blocks_replacement()
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        await using ApplicationDbContext context = db.NewContext();
        Hall other = Hall.Create(EntityId<Hall>.New(), "Other hall");
        Session session = Session.Create(EntityId<Session>.New(), DateTime.UtcNow.AddDays(1),
            DateTime.UtcNow.AddDays(1).AddHours(2), db.Movie.Id, other.Id, db.Pricing.Id);
        User user = new() { Id = Guid.NewGuid(), UserName = "layout-test" };
        context.Halls.Add(other);
        context.Sessions.Add(session);
        context.Users.Add(user);
        context.SeatLocks.Add(SeatLock.New(EntityId<SeatLock>.New(), DateTime.UtcNow.AddMinutes(10),
            session.Id, db.Hall.Seats.First().Id, user.Id));
        await context.SaveChangesAsync(Ct);

        (await Replace(context, Command(db))).Error.Code.Should().Be("Hall.LayoutLocked");
        await AssertOriginal(db);
        (await context.SeatLocks.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Failure_after_database_write_rolls_back_deletes_inserts_and_capacity()
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        await using ApplicationDbContext context = db.NewContext(new FailAfterSave());

        Func<Task> replace = () => Replace(context, Command(db));

        await replace.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected failure after SQL writes");
        context.ChangeTracker.Entries().Should().BeEmpty();
        await AssertOriginal(db);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_edit_and_session_creation_serialize_in_both_orders(bool editFirst)
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        PauseBeforeSave pause = new();
        await using ApplicationDbContext first = db.NewContext(pause);
        await using ApplicationDbContext second = db.NewContext();
        await second.Database.OpenConnectionAsync(Ct);
        int waitingPid = ((NpgsqlConnection)second.Database.GetDbConnection()).ProcessID;
        Task<Result> edit;
        Task<Result<Guid>> create;
        if (editFirst)
        {
            edit = Replace(first, Command(db));
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            create = CreateSession(second, db);
        }
        else
        {
            create = CreateSession(first, db);
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            edit = Replace(second, Command(db));
        }
        try
        {
            await WaitForDatabaseLock(db, waitingPid);
            (editFirst ? create.IsCompleted : edit.IsCompleted).Should().BeFalse();
        }
        finally
        {
            pause.Release.TrySetResult();
            await Task.WhenAll(edit, create).WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        (await create).IsSuccess.Should().BeTrue();
        if (editFirst) (await edit).IsSuccess.Should().BeTrue();
        else
        {
            (await edit).Error.Code.Should().Be("Hall.LayoutLocked");
            await AssertOriginal(db);
        }
        await using ApplicationDbContext verify = db.NewContext();
        (await verify.Sessions.CountAsync(Ct)).Should().Be(1);
        (await Replace(verify, Command(db))).Error.Code.Should().Be("Hall.LayoutLocked");
    }

    [Theory]
    [InlineData("hall", "Session.HallNotFound")]
    [InlineData("pricing", "Session.PricingNotFound")]
    [InlineData("movie", "Session.Validation")]
    public async Task Session_creation_preserves_existing_failures(string invalid, string error)
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        await using ApplicationDbContext context = db.NewContext();
        IMovieInfoProvider provider = Substitute.For<IMovieInfoProvider>();
        provider.GetDurationMinutesAsync(Arg.Any<EntityId<Cinema.Catalog.Domain.Entities.Movie>>(), Arg.Any<CancellationToken>())
            .Returns(invalid == "movie" ? (int?)null : 90);
        var command = new CreateSessionCommand(db.Movie.Id.Value,
            invalid == "hall" ? Guid.NewGuid() : db.Hall.Id.Value,
            invalid == "pricing" ? Guid.NewGuid() : db.Pricing.Id.Value, DateTime.UtcNow.AddDays(3));
        var result = await new CreateSessionCommandHandler(new SessionSchedulingService(context, provider), context)
            .Handle(command, Ct);
        result.Error.Code.Should().Be(error);
        (await context.Sessions.CountAsync(Ct)).Should().Be(0);
    }

    private static Task<Result<Guid>> CreateSession(ApplicationDbContext context, HallLayoutDatabase db)
    {
        IMovieInfoProvider provider = Substitute.For<IMovieInfoProvider>();
        provider.GetDurationMinutesAsync(db.Movie.Id, Arg.Any<CancellationToken>()).Returns(90);
        return new CreateSessionCommandHandler(new SessionSchedulingService(context, provider), context)
            .Handle(new(db.Movie.Id.Value, db.Hall.Id.Value, db.Pricing.Id.Value, DateTime.UtcNow.AddDays(3)), Ct);
    }

    private static async Task AssertOriginal(HallLayoutDatabase db)
    {
        await using ApplicationDbContext verify = db.NewContext();
        Hall hall = await verify.Halls.IgnoreQueryFilters().Include(h => h.Seats).SingleAsync(h => h.Id == db.Hall.Id, Ct);
        hall.TotalCapacity.Should().Be(6);
        hall.Seats.Select(s => (s.Id, s.SeatTypeId, s.GridX, s.GridY, s.Number))
            .Should().BeEquivalentTo(db.Hall.Seats.Select(s => (s.Id, s.SeatTypeId, s.GridX, s.GridY, s.Number)));
    }

    private static async Task WaitForDatabaseLock(HallLayoutDatabase db, int pid)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using ApplicationDbContext monitor = db.NewContext();
        while (!await monitor.Database.SqlQuery<bool>(
            $"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {pid} AND wait_event_type = 'Lock') AS \"Value\"")
            .SingleAsync(timeout.Token))
            await Task.Delay(20, timeout.Token);
    }

    [Fact]
    public async Task Session_creation_failure_after_write_rolls_back_and_returns_existing_error()
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        await using ApplicationDbContext context = db.NewContext(new FailAfterSave());

        (await CreateSession(context, db)).Error.Code.Should().Be("Session.GeneralError");

        context.ChangeTracker.Entries().Should().BeEmpty();
        await using ApplicationDbContext verify = db.NewContext();
        (await verify.Sessions.CountAsync(Ct)).Should().Be(0);
        await AssertOriginal(db);
    }

    [Fact]
    public async Task Transient_failure_retries_entire_replacement_with_a_fresh_tracked_graph()
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        FailOnceAfterSave failure = new();
        await using ApplicationDbContext context = db.NewContext(failure);

        (await Replace(context, Command(db))).IsSuccess.Should().BeTrue();

        failure.Attempts.Should().Be(2);
        await using ApplicationDbContext verify = db.NewContext();
        (await verify.Seats.CountAsync(Ct)).Should().Be(3);
        (await verify.Halls.SingleAsync(Ct)).TotalCapacity.Should().Be(3);
    }

    private sealed class FailOnceAfterSave : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts == 1) throw new NpgsqlException("Injected transient failure", new IOException());
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Injected failure after SQL writes");
    }

    private sealed class PauseBeforeSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }
}
