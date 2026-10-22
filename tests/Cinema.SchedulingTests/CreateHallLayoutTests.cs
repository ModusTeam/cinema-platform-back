using Cinema.Application.Halls.Commands.CreateHall;
using Cinema.Application.Halls.Queries.GetHallById;
using Cinema.Application;
using Cinema.Application.Common.Interfaces;
using Cinema.Catalog.Domain.Entities;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Services;
using Cinema.Infrastructure.Persistence;
using Cinema.Scheduling.Application;
using FluentAssertions;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MediatR;
using Xunit;

namespace Cinema.SchedulingTests;

public class CreateHallLayoutTests
{
    private static readonly Guid StandardId = Guid.NewGuid();
    private static readonly Guid PremiumId = Guid.NewGuid();

    [Fact]
    public async Task Omitted_seats_create_the_original_rectangle()
    {
        await using ApplicationDbContext context = CreateContext();
        await SeedSeatTypes(context);

        var result = await Create(context, Command(rows: 2, columns: 3));

        result.IsSuccess.Should().BeTrue();
        Hall hall = await context.Halls.Include(h => h.Seats)
            .SingleAsync(TestContext.Current.CancellationToken);
        hall.TotalCapacity.Should().Be(6);
        hall.Seats.Select(s => (s.GridX, s.GridY, s.Number, s.RowLabel))
            .Should().BeEquivalentTo(new[]
            {
                (1, 1, 1, "1"), (2, 1, 2, "1"), (3, 1, 3, "1"),
                (1, 2, 1, "2"), (2, 2, 2, "2"), (3, 2, 3, "2")
            });
    }

    [Fact]
    public async Task Sparse_layout_preserves_gaps_types_and_consecutive_numbering()
    {
        await using ApplicationDbContext context = CreateContext();
        await SeedSeatTypes(context);
        CreateHallSeat[] supplied =
        [
            new(6, 1, StandardId), new(1, 2, PremiumId),
            new(2, 1, PremiumId), new(4, 1, StandardId)
        ];

        var result = await Create(context, Command(seats: [.. supplied]));

        result.IsSuccess.Should().BeTrue();
        Hall hall = await context.Halls.Include(h => h.Seats)
            .SingleAsync(TestContext.Current.CancellationToken);
        hall.TotalCapacity.Should().Be(4);
        hall.Seats.Select(s => (s.GridX, s.GridY, s.Number, s.RowLabel, s.SeatTypeId.Value))
            .Should().BeEquivalentTo(new[]
            {
                (2, 1, 1, "1", PremiumId), (4, 1, 2, "1", StandardId),
                (6, 1, 3, "1", StandardId), (1, 2, 1, "2", PremiumId)
            });
        hall.Seats.Select(s => s.Id.Value).Distinct().Should().HaveCount(4);
        hall.Seats.All(s => s.HallId == hall.Id).Should().BeTrue();

        TypeAdapterConfig.GlobalSettings.Scan(typeof(CreateHallCommand).Assembly);
        var getResult = await new GetHallByIdQueryHandler(context)
            .Handle(new GetHallByIdQuery(hall.Id.Value), CancellationToken.None);
        getResult.IsSuccess.Should().BeTrue();
        getResult.Value.TotalCapacity.Should().Be(4);
        getResult.Value.Seats!.Select(s => (s.GridX, s.GridY, s.SeatTypeId))
            .Should().BeEquivalentTo(supplied.Select(s => (s.GridX, s.GridY, s.SeatTypeId)));
    }

    [Fact]
    public void Sparse_numbering_is_independent_of_input_order()
    {
        SeatLayoutService service = new();
        EntityId<Hall> hallId = EntityId<Hall>.New();
        var coordinates = new[]
        {
            (GridX: 6, GridY: 1, SeatTypeId: new EntityId<SeatType>(StandardId)),
            (GridX: 2, GridY: 1, SeatTypeId: new EntityId<SeatType>(PremiumId)),
            (GridX: 4, GridY: 1, SeatTypeId: new EntityId<SeatType>(StandardId))
        };

        var forward = service.GenerateSparseLayout(coordinates, hallId);
        var reversed = service.GenerateSparseLayout(coordinates.Reverse(), hallId);

        forward.Select(s => (s.GridX, s.GridY, s.Number))
            .Should().Equal(reversed.Select(s => (s.GridX, s.GridY, s.Number)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(7, 1)]
    [InlineData(1, 5)]
    public void Invalid_coordinates_are_rejected(int gridX, int gridY)
    {
        CreateHallCommand command = Command(seats: [new(gridX, gridY, StandardId)]);

        new CreateHallCommandValidator().Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Empty_duplicate_and_invalid_seat_type_inputs_are_rejected()
    {
        CreateHallCommandValidator validator = new();

        validator.Validate(Command(seats: [])).IsValid.Should().BeFalse();
        validator.Validate(Command(seats: [new(1, 1, StandardId), new(1, 1, PremiumId)]))
            .IsValid.Should().BeFalse();
        validator.Validate(Command(seats: [new(1, 1, Guid.Empty)]))
            .IsValid.Should().BeFalse();
        validator.Validate(Command(technologies: [Guid.NewGuid(), Guid.Empty]))
            .IsValid.Should().BeFalse();
        Guid technologyId = Guid.NewGuid();
        validator.Validate(Command(technologies: [technologyId, technologyId]))
            .IsValid.Should().BeFalse();
        validator.Validate(Command() with { Name = "   " }).IsValid.Should().BeFalse();
        validator.Validate(Command(rows: 1_001, seats: [new(1, 1, StandardId)]))
            .IsValid.Should().BeFalse();
        validator.Validate(Command(columns: 1_001, seats: [new(1, 1, StandardId)]))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_seat_type_does_not_persist_a_hall()
    {
        await using ApplicationDbContext context = CreateContext();
        await SeedSeatTypes(context);

        var result = await Create(context, Command(seats: [new(1, 1, Guid.NewGuid())]));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SeatType.NotFound");
        (await context.Halls.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await context.Seats.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Unknown_default_type_and_technology_do_not_persist_a_hall()
    {
        await using ApplicationDbContext context = CreateContext();
        await SeedSeatTypes(context);
        CreateHallCommand invalidDefault = Command(seats: [new(1, 1, StandardId)])
            with { SeatTypeId = Guid.NewGuid() };

        (await Create(context, invalidDefault)).Error.Code.Should().Be("SeatType.NotFound");
        (await Create(context, Command(technologies: [Guid.NewGuid()]))).Error.Code
            .Should().Be("Technology.NotFound");
        (await context.Halls.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Invalid_sparse_request_is_rejected_before_persistence()
    {
        await using ApplicationDbContext context = CreateContext();
        await SeedSeatTypes(context);
        using ServiceProvider provider = new ServiceCollection()
            .AddLogging()
            .AddApplication()
            .AddSchedulingApplication()
            .AddSingleton<IApplicationDbContext>(context)
            .BuildServiceProvider();
        IMediator mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.Send(Command(seats: []), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Validation.Seats");
        (await context.Halls.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    private static CreateHallCommand Command(int rows = 4, int columns = 6,
        List<CreateHallSeat>? seats = null, List<Guid>? technologies = null)
        => new("Test hall", rows, columns, StandardId, technologies ?? [], seats);

    private static async Task<Cinema.Domain.Shared.Result<Guid>> Create(
        ApplicationDbContext context, CreateHallCommand command)
        => await new CreateHallCommandHandler(context, new SeatLayoutService())
            .Handle(command, CancellationToken.None);

    private static async Task SeedSeatTypes(ApplicationDbContext context)
    {
        context.SeatTypes.AddRange(
            SeatType.New(new EntityId<SeatType>(StandardId), "Standard", null),
            SeatType.New(new EntityId<SeatType>(PremiumId), "Premium", null));
        await context.SaveChangesAsync();
    }

    private static ApplicationDbContext CreateContext()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new HallLayoutDbContext(options);
    }

    private sealed class HallLayoutDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Movie>().Ignore(movie => movie.Embedding);
        }
    }
}
