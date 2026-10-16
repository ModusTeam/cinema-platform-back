using System.Data.Common;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Movies.Queries.GetRecommendations;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NSubstitute;
using Pgvector;
using Xunit;

namespace Cinema.OrdersTests;

// Uses the same disposable cinema_orders_test_* convention as OrderReservationPostgresTests.
public class RecommendationTicketAccessPostgresTests(PostgresSchemaTemplate template) : IClassFixture<PostgresSchemaTemplate>
{
    [Fact]
    public void Ticket_Correlation_Translates_To_Filtered_Exists_Sql()
    {
        using ApplicationDbContext context = NewSqlOnlyContext();
        Guid userId = Guid.NewGuid();
        string oldVectors = context.Orders.AsNoTracking()
            .Where(o => o.UserId == userId && o.Status == OrderStatus.Paid)
            .Join(context.Tickets, o => o.Id, t => t.OrderId, (_, t) => t).Select(t => t.Session!.Movie!)
            .Where(m => m.Embedding != null).Select(m => m.Embedding!).ToQueryString();
        string newVectors = context.Tickets.AsNoTracking()
            .Where(t => context.Orders.Any(o => o.Id == t.OrderId && o.UserId == userId && o.Status == OrderStatus.Paid))
            .Select(t => t.Session!.Movie!).Where(m => m.Embedding != null).Select(m => m.Embedding!).ToQueryString();
        string oldWatched = context.Orders.Where(o => o.UserId == userId)
            .Join(context.Tickets, o => o.Id, t => t.OrderId, (_, t) => t).Select(t => t.Session!.MovieId).Distinct().ToQueryString();
        string newWatched = context.Tickets.Where(t => context.Orders.Any(o => o.Id == t.OrderId && o.UserId == userId))
            .Select(t => t.Session!.MovieId).Distinct().ToQueryString();

        oldVectors.Should().Contain("FROM orders AS").And.Contain("o.status = 1");
        newVectors.Should().Contain("FROM tickets AS").And.Contain("EXISTS").And.Contain("o.status = 1").And.Contain("is_active");
        oldWatched.Should().Contain("SELECT DISTINCT").And.NotContain("o.status = 1");
        newWatched.Should().Contain("SELECT DISTINCT").And.Contain("EXISTS").And.NotContain("o.status = 1");
    }

    [Fact]
    public async Task Unauthenticated_User_Returns_Auth_Required_Without_Queries()
    {
        CommandCounter counter = new();
        await using ApplicationDbContext context = NewSqlOnlyContext(counter);
        ICurrentUserService user = Substitute.For<ICurrentUserService>();
        user.UserId.Returns((Guid?)null);

        Result<List<MovieRecommendationResult>> result = await new GetPersonalizedRecommendationsQueryHandler(context, user)
            .Handle(new GetPersonalizedRecommendationsQuery(), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Auth.Required");
        counter.Commands.Should().BeEmpty();
    }

    private static ApplicationDbContext NewSqlOnlyContext(DbCommandInterceptor? interceptor = null)
    {
        DbContextOptionsBuilder<ApplicationDbContext> builder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=cinema_orders_test_sql;Username=postgres", x => x.UseVector())
            .UseSnakeCaseNamingConvention();
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return new ApplicationDbContext(builder.Options);
    }

    [Fact]
    public async Task No_Paid_Visible_Embedded_Tickets_Returns_Empty_After_One_Query()
    {
        await using RecommendationDatabase database = await RecommendationDatabase.CreateAsync(template);
        await database.AddTicketsAsync(database.UserId, database.AddMovie("Pending", [1f, 0f]), OrderStatus.Pending, 1);
        await database.AddTicketsAsync(database.UserId, database.AddMovie("Null", null), OrderStatus.Paid, 1);
        CommandCounter counter = new();
        await using ApplicationDbContext context = database.NewContext(counter);

        Result<List<MovieRecommendationResult>> result = await HandleAsync(context, database.UserId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        counter.Commands.Should().HaveCount(1);
    }

    [Fact]
    public async Task Ticket_Queries_Match_Order_Joins_And_Preserve_Recommendations()
    {
        await using RecommendationDatabase database = await RecommendationDatabase.CreateAsync(template);
        Movie paidA = database.AddMovie("Paid A", [1f, 0f]);
        Movie paidB = database.AddMovie("Paid B", [0f, 1f]);
        Movie pending = database.AddMovie("Pending", [1f, 0f]);
        Movie failed = database.AddMovie("Failed", [1f, 0f]);
        Movie cancelled = database.AddMovie("Cancelled", [1f, 0f]);
        Movie nullEmbedding = database.AddMovie("Null embedding", null);
        Movie inactive = database.AddMovie("Inactive hall", [-1f, 0f]);
        Movie foreign = database.AddMovie("Other user", [-1f, 0f]);
        Movie best = database.AddMovie("Best", [2f, 1f]);
        Movie second = database.AddMovie("Second", [0f, 1f]);
        await database.AddTicketsAsync(database.UserId, paidA, OrderStatus.Paid, 2);
        await database.AddTicketsAsync(database.UserId, paidB, OrderStatus.Paid, 1);
        await database.AddTicketsAsync(database.UserId, pending, OrderStatus.Pending, 2);
        await database.AddTicketsAsync(database.UserId, failed, OrderStatus.Failed, 1);
        await database.AddTicketsAsync(database.UserId, cancelled, OrderStatus.Cancelled, 1);
        await database.AddTicketsAsync(database.UserId, nullEmbedding, OrderStatus.Paid, 1);
        await database.AddTicketsAsync(database.UserId, inactive, OrderStatus.Paid, 1, inactiveHall: true);
        await database.AddTicketsAsync(database.OtherUserId, foreign, OrderStatus.Paid, 1);

        await using ApplicationDbContext comparison = database.NewContext();
        IQueryable<Vector> oldVectors = comparison.Orders.AsNoTracking()
            .Where(o => o.UserId == database.UserId && o.Status == OrderStatus.Paid)
            .Join(comparison.Tickets, o => o.Id, t => t.OrderId, (_, t) => t).Select(t => t.Session!.Movie!)
            .Where(m => m.Embedding != null).Select(m => m.Embedding!);
        IQueryable<Vector> newVectors = comparison.Tickets.AsNoTracking()
            .Where(t => comparison.Orders.Any(o => o.Id == t.OrderId && o.UserId == database.UserId && o.Status == OrderStatus.Paid))
            .Select(t => t.Session!.Movie!).Where(m => m.Embedding != null).Select(m => m.Embedding!);
        IQueryable<EntityId<Movie>> oldWatched = comparison.Orders.Where(o => o.UserId == database.UserId)
            .Join(comparison.Tickets, o => o.Id, t => t.OrderId, (_, t) => t).Select(t => t.Session!.MovieId).Distinct();
        IQueryable<EntityId<Movie>> newWatched = comparison.Tickets
            .Where(t => comparison.Orders.Any(o => o.Id == t.OrderId && o.UserId == database.UserId))
            .Select(t => t.Session!.MovieId).Distinct();

        oldVectors.ToQueryString().Should().Contain("FROM orders AS").And.Contain("o.status = 1");
        newVectors.ToQueryString().Should().Contain("FROM tickets AS").And.Contain("EXISTS").And.Contain("o.status = 1").And.Contain("is_active");
        oldWatched.ToQueryString().Should().Contain("SELECT DISTINCT").And.NotContain("o.status = 1");
        newWatched.ToQueryString().Should().Contain("SELECT DISTINCT").And.Contain("EXISTS").And.NotContain("o.status = 1");

        List<float[]> expectedVectors = (await oldVectors.ToListAsync(TestContext.Current.CancellationToken))
            .Select(v => v.ToArray()).OrderBy(v => v[0]).ThenBy(v => v[1]).ToList();
        List<float[]> actualVectors = (await newVectors.ToListAsync(TestContext.Current.CancellationToken))
            .Select(v => v.ToArray()).OrderBy(v => v[0]).ThenBy(v => v[1]).ToList();
        actualVectors.Should().BeEquivalentTo(expectedVectors, options => options.WithStrictOrdering());
        actualVectors.Should().HaveCount(3);
        actualVectors.Count(v => v[0] == 1f && v[1] == 0f).Should().Be(2);

        List<EntityId<Movie>> expectedWatched = await oldWatched.ToListAsync(TestContext.Current.CancellationToken);
        List<EntityId<Movie>> actualWatched = await newWatched.ToListAsync(TestContext.Current.CancellationToken);
        actualWatched.Should().BeEquivalentTo(expectedWatched);
        actualWatched.Should().HaveCount(6);
        actualWatched.Should().Contain([paidA.Id, paidB.Id, pending.Id, failed.Id, cancelled.Id, nullEmbedding.Id]);
        actualWatched.Should().NotContain([inactive.Id, foreign.Id]);

        CommandCounter counter = new();
        await using ApplicationDbContext queryContext = database.NewContext(counter);
        Result<List<MovieRecommendationResult>> result = await HandleAsync(queryContext, database.UserId, 2);
        result.IsSuccess.Should().BeTrue();
        result.Value.Select(m => m.Id).Should().Equal(best.Id.Value, second.Id.Value);
        result.Value[0].Title.Should().Be("Best");
        result.Value[0].PosterUrl.Should().BeNull();
        result.Value[0].SimilarityScore.Should().BeApproximately(1d, 0.00001d);
        result.Value[1].SimilarityScore.Should().BeApproximately(1d / Math.Sqrt(5d), 0.00001d);
        counter.Commands.Should().HaveCount(3);
        counter.Commands[0].Should().Contain("FROM tickets").And.Contain("EXISTS");
        counter.Commands[1].Should().Contain("SELECT DISTINCT").And.Contain("FROM tickets");
        counter.Commands[2].Should().Contain("<=>");

        (await HandleAsync(queryContext, database.UserId, 1)).Value.Select(m => m.Id).Should().Equal(best.Id.Value);
        (await HandleAsync(queryContext, database.UserId, 0)).Value.Should().BeEmpty();
    }

    private static async Task<Result<List<MovieRecommendationResult>>> HandleAsync(ApplicationDbContext context, Guid userId, int count = 5)
    {
        ICurrentUserService user = Substitute.For<ICurrentUserService>();
        user.UserId.Returns(userId);
        return await new GetPersonalizedRecommendationsQueryHandler(context, user)
            .Handle(new GetPersonalizedRecommendationsQuery(count), TestContext.Current.CancellationToken);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class RecommendationDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly string _adminConnectionString;
        private readonly string _databaseName;
        private readonly Hall _activeHall = Hall.Create(EntityId<Hall>.New(), "Recommendation active hall");
        private readonly Hall _inactiveHall = Hall.Create(EntityId<Hall>.New(), "Recommendation inactive hall");
        private readonly SeatType _seatType = SeatType.New(EntityId<SeatType>.New(), "Recommendation standard", null);
        private readonly Pricing _pricing = Pricing.New(EntityId<Pricing>.New(), "Recommendation pricing");
        private int _seatNumber;

        public ApplicationDbContext Context { get; }
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid OtherUserId { get; } = Guid.NewGuid();

        private RecommendationDatabase(string connectionString, string adminConnectionString, string databaseName)
        {
            _connectionString = connectionString;
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            Context = NewContext();
        }

        public static async Task<RecommendationDatabase> CreateAsync(PostgresSchemaTemplate template)
        {
            (string connectionString, string adminConnectionString, string databaseName) =
                await template.CreateDatabaseAsync();
            RecommendationDatabase database = new(connectionString, adminConnectionString, databaseName);
            try
            {
                database.Context.Users.AddRange(
                    new User { Id = database.UserId, UserName = $"recommendation-{Guid.NewGuid():N}" },
                    new User { Id = database.OtherUserId, UserName = $"recommendation-{Guid.NewGuid():N}" });
                database.Context.Halls.AddRange(database._activeHall, database._inactiveHall);
                database.Context.SeatTypes.Add(database._seatType);
                database.Context.Pricings.Add(database._pricing);
                await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public ApplicationDbContext NewContext(DbCommandInterceptor? interceptor = null)
        {
            DbContextOptionsBuilder<ApplicationDbContext> builder = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(_connectionString, x => x.UseVector()).UseSnakeCaseNamingConvention();
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return new ApplicationDbContext(builder.Options);
        }

        public Movie AddMovie(string title, float[]? embedding)
        {
            Movie movie = Movie.CreateManual(title, "Recommendation test", 90, DateTime.UtcNow.Year, MovieStatus.ComingSoon);
            if (embedding is not null) movie.SetEmbedding(embedding);
            Context.Movies.Add(movie);
            return movie;
        }

        public async Task AddTicketsAsync(Guid userId, Movie movie, OrderStatus status, int ticketCount, bool inactiveHall = false)
        {
            Hall hall = inactiveHall ? _inactiveHall : _activeHall;
            Session session = Session.Create(EntityId<Session>.New(), DateTime.UtcNow.AddDays(2),
                DateTime.UtcNow.AddDays(2).AddHours(2), movie.Id, hall.Id, _pricing.Id);
            Context.Sessions.Add(session);
            Order order = Order.New(EntityId<Order>.New(), ticketCount * 50m, userId, session.Id);
            switch (status)
            {
                case OrderStatus.Paid: order.MarkAsPaid("recommendation-test"); break;
                case OrderStatus.Failed: order.MarkAsFailed(); break;
                case OrderStatus.Cancelled: order.MarkAsCancelled(); break;
            }
            Context.Orders.Add(order);
            for (int i = 0; i < ticketCount; i++)
            {
                int number = ++_seatNumber;
                Seat seat = Seat.New(EntityId<Seat>.New(), "R", number, number, 1, SeatStatus.Active, hall.Id, _seatType.Id);
                Context.Seats.Add(seat);
                Context.Tickets.Add(Ticket.New(EntityId<Ticket>.New(), 50m, TicketStatus.Valid, order.Id, session.Id, seat.Id));
            }
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            if (inactiveHall)
            {
                hall.Deactivate();
                await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await using NpgsqlConnection admin = new(_adminConnectionString);
            await admin.OpenAsync();
            await using NpgsqlCommand drop = new($"DROP DATABASE {_databaseName} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
