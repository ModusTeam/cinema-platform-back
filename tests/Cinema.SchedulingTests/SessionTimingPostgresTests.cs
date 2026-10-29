using Cinema.Application.Services;
using Cinema.Application.Common.Mappings.Sessions;
using Cinema.Application.Sessions.Commands.CreateSession;
using Cinema.Application.Sessions.Dtos;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Interfaces;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using Cinema.OrdersTests;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mapster;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Cinema.SchedulingTests;

public class SessionTimingPostgresTests(PostgresSchemaTemplate template) : IClassFixture<PostgresSchemaTemplate>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Screening_and_occupancy_persist_separately_and_reschedule_preserves_both()
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        DateTime start = DateTime.UtcNow.Date.AddDays(3).AddHours(12);
        await using (ApplicationDbContext context = db.NewContext())
            (await Create(context, db, start)).IsSuccess.Should().BeTrue();

        await using (ApplicationDbContext context = db.NewContext())
        {
            Session session = await context.Sessions.SingleAsync(Ct);
            session.EndTime.Should().Be(start.AddMinutes(90));
            session.OccupancyEndTime.Should().Be(start.AddMinutes(105));
            await new SessionSchedulingService(context, Provider(db)).RescheduleSessionAsync(session, start.AddDays(1), Ct);
            await context.SaveChangesAsync(Ct);
        }
        await using ApplicationDbContext verify = db.NewContext();
        Session moved = await verify.Sessions.SingleAsync(Ct);
        moved.EndTime.Should().Be(start.AddDays(1).AddMinutes(90));
        moved.OccupancyEndTime.Should().Be(start.AddDays(1).AddMinutes(105));
        TypeAdapterConfig mapping = new();
        new SessionMappingConfig().Register(mapping);
        SessionDto dto = await verify.Sessions.ProjectToType<SessionDto>(mapping).SingleAsync(Ct);
        dto.EndTime.Should().Be(moved.EndTime);
        dto.OccupancyEndTime.Should().Be(moved.OccupancyEndTime);
        Func<Task> invalid = () => verify.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sessions SET end_time = {start.AddDays(1).AddMinutes(106)} WHERE id = {moved.Id.Value}", Ct);
        (await invalid.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Theory]
    [InlineData(90, false)]
    [InlineData(105, true)]
    public async Task Concurrent_creates_serialize_and_preserve_the_cleaning_gap(int secondOffset, bool secondSucceeds)
    {
        await using HallLayoutDatabase db = await HallLayoutDatabase.CreateAsync(template);
        DateTime start = DateTime.UtcNow.Date.AddDays(3).AddHours(12);
        PauseSave pause = new();
        await using ApplicationDbContext first = db.NewContext(pause);
        await using ApplicationDbContext second = db.NewContext();
        await second.Database.OpenConnectionAsync(Ct);
        int pid = ((NpgsqlConnection)second.Database.GetDbConnection()).ProcessID;
        Task<Result<Guid>> firstCreate = Create(first, db, start);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Task<Result<Guid>> secondCreate = Create(second, db, start.AddMinutes(secondOffset));
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using ApplicationDbContext monitor = db.NewContext();
            while (!await monitor.Database.SqlQuery<bool>(
                $"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {pid} AND wait_event_type = 'Lock') AS \"Value\"")
                .SingleAsync(timeout.Token))
                await Task.Delay(20, timeout.Token);
            secondCreate.IsCompleted.Should().BeFalse();
        }
        finally
        {
            pause.Release.TrySetResult();
            await Task.WhenAll(firstCreate, secondCreate).WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        (await firstCreate).IsSuccess.Should().BeTrue();
        (await secondCreate).IsSuccess.Should().Be(secondSucceeds);
        if (!secondSucceeds) (await secondCreate).Error.Code.Should().Be("Session.Validation");
        await using ApplicationDbContext verify = db.NewContext();
        (await verify.Sessions.CountAsync(Ct)).Should().Be(secondSucceeds ? 2 : 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(90)]
    [InlineData(105)]
    public async Task Migration_preserves_recorded_legacy_boundary_without_inventing_screening_end(int? legacyMinutes)
    {
        string? connection = Environment.GetEnvironmentVariable("CINEMA_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Skip("Set CINEMA_TEST_POSTGRES_CONNECTION to a disposable cinema_orders_test_* database.");
        NpgsqlConnectionStringBuilder builder = new(connection);
        if (builder.Database?.StartsWith("cinema_orders_test_", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Not a disposable test target");
        string databaseName = $"cinema_orders_test_timing_{Guid.NewGuid():N}";
        builder.Database = "postgres";
        await using NpgsqlConnection admin = new(builder.ConnectionString);
        await admin.OpenAsync(Ct);
        await using (NpgsqlCommand create = new($"CREATE DATABASE {databaseName}", admin))
            await create.ExecuteNonQueryAsync(Ct);
        builder.Database = databaseName;
        try
        {
            await using NpgsqlDataSource source = DatabaseContextConfiguration.CreateDataSource(builder.ConnectionString);
            DbContextOptionsBuilder<ApplicationDbContext> options = new();
            DatabaseContextConfiguration.Configure(options, source);
            DateTime start = DateTime.UtcNow.Date.AddDays(3).AddHours(12);
            Guid sessionId = Guid.NewGuid();
            await using (ApplicationDbContext context = new(options.Options))
            {
                if (legacyMinutes.HasValue)
                {
                    await context.GetService<IMigrator>().MigrateAsync("20261026143029_AddCustomerFactsSource", Ct);
                    Movie movie = Movie.CreateManual("Timing", "Test", 90, 2026, MovieStatus.ComingSoon);
                    Hall hall = Hall.Create(EntityId<Hall>.New(), "Timing");
                    Pricing pricing = Pricing.New(EntityId<Pricing>.New(), "Timing");
                    context.Movies.Add(movie);
                    context.Halls.Add(hall);
                    context.Pricings.Add(pricing);
                    await context.SaveChangesAsync(Ct);
                    await context.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO sessions (id,start_time,end_time,status,movie_id,hall_id,pricing_id,is_loyalty_payment_allowed,event_type)
                        VALUES ({sessionId},{start},{start.AddMinutes(legacyMinutes.Value)},0,{movie.Id.Value},{hall.Id.Value},{pricing.Id.Value},true,'STANDARD')
                        """, Ct);
                }
                await context.Database.MigrateAsync(Ct);
                (await context.Database.GetPendingMigrationsAsync(Ct)).Should().BeEmpty();
                context.Database.HasPendingModelChanges().Should().BeFalse();
            }
            await using ApplicationDbContext verify = new(options.Options);
            string indexDefinition = await verify.Database.SqlQuery<string>($"""
                SELECT indexdef AS "Value" FROM pg_indexes
                WHERE schemaname = 'public' AND indexname = 'IX_Sessions_Hall_Time_Overlap'
                """).SingleAsync(Ct);
            indexDefinition.Should().Contain("occupancy_end_time");
            if (legacyMinutes.HasValue)
            {
                Session legacy = await verify.Sessions.SingleAsync(Ct);
                legacy.EndTime.Should().BeNull();
                legacy.OccupancyEndTime.Should().Be(start.AddMinutes(legacyMinutes.Value));
                await new SessionSchedulingService(verify, Substitute.For<IMovieInfoProvider>())
                    .RescheduleSessionAsync(legacy, start.AddDays(1), Ct);
                await verify.SaveChangesAsync(Ct);
                verify.ChangeTracker.Clear();
                Session reloaded = await verify.Sessions.SingleAsync(Ct);
                reloaded.EndTime.Should().BeNull();
                reloaded.OccupancyEndTime.Should().Be(start.AddDays(1).AddMinutes(legacyMinutes.Value));
            }
            else (await verify.Sessions.CountAsync(Ct)).Should().Be(0);
        }
        finally
        {
            await using NpgsqlCommand drop = new($"DROP DATABASE {databaseName} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static IMovieInfoProvider Provider(HallLayoutDatabase db)
    {
        IMovieInfoProvider provider = Substitute.For<IMovieInfoProvider>();
        provider.GetDurationMinutesAsync(db.Movie.Id, Arg.Any<CancellationToken>()).Returns(90);
        return provider;
    }

    private static Task<Result<Guid>> Create(ApplicationDbContext context, HallLayoutDatabase db, DateTime start)
        => new CreateSessionCommandHandler(new SessionSchedulingService(context, Provider(db)), context)
            .Handle(new(db.Movie.Id.Value, db.Hall.Id.Value, db.Pricing.Id.Value, start), Ct);

    private sealed class PauseSave : SaveChangesInterceptor
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
