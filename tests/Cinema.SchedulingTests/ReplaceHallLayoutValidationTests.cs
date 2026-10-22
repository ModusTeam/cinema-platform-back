using Cinema.Application.Halls.Commands.CreateHall;
using Cinema.Application.Common.Behaviours;
using Cinema.Application.Halls.Commands.ReplaceHallLayout;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Exceptions;
using Cinema.Domain.Services;
using Cinema.Domain.Shared;
using FluentAssertions;
using Xunit;

namespace Cinema.SchedulingTests;

public class ReplaceHallLayoutValidationTests
{
    private static readonly Guid TypeId = Guid.NewGuid();
    private static ReplaceHallLayoutCommand Command(List<CreateHallSeat> seats)
        => new(Guid.NewGuid(), 5, 8, seats);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(9, 1)]
    [InlineData(1, 6)]
    public void Invalid_coordinates_are_rejected(int x, int y)
        => new ReplaceHallLayoutCommandValidator().Validate(Command([new(x, y, TypeId)]))
            .IsValid.Should().BeFalse();

    [Fact]
    public void Empty_null_duplicate_missing_type_and_oversized_layouts_are_rejected()
    {
        ReplaceHallLayoutCommandValidator validator = new();
        validator.Validate(Command([])).IsValid.Should().BeFalse();
        validator.Validate(Command(null!)).IsValid.Should().BeFalse();
        validator.Validate(Command([null!])).IsValid.Should().BeFalse();
        validator.Validate(Command([new(1, 1, TypeId), new(1, 1, TypeId)]))
            .IsValid.Should().BeFalse();
        validator.Validate(Command([new(1, 1, Guid.Empty)])).IsValid.Should().BeFalse();
        validator.Validate(Command([new(1, 1, TypeId)]) with { HallId = Guid.Empty })
            .IsValid.Should().BeFalse();
        foreach (int dimension in new[] { 0, -1, 1_001 })
        {
            validator.Validate(Command([new(1, 1, TypeId)]) with { Rows = dimension })
                .IsValid.Should().BeFalse();
            validator.Validate(Command([new(1, 1, TypeId)]) with { SeatsPerRow = dimension })
                .IsValid.Should().BeFalse();
        }
        validator.Validate(Command(Enumerable.Repeat(new CreateHallSeat(1, 1, TypeId), 10_001).ToList()))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Valid_sparse_layout_uses_the_same_rules_as_creation()
    {
        var command = Command([new(8, 5, TypeId), new(2, 1, TypeId)]);
        new ReplaceHallLayoutCommandValidator().Validate(command).IsValid.Should().BeTrue();
        new CreateHallCommandValidator().Validate(new CreateHallCommand("Hall", 5, 8, TypeId, [], command.Seats))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Invalid_layout_is_rejected_before_the_handler_runs()
    {
        bool called = false;
        var behavior = new ValidationBehavior<ReplaceHallLayoutCommand, Result>([new ReplaceHallLayoutCommandValidator()]);
        Result result = await behavior.Handle(Command([]), _ =>
        {
            called = true;
            return Task.FromResult(Result.Success());
        }, TestContext.Current.CancellationToken);

        called.Should().BeFalse();
        result.Error.Should().Be(new Error("Validation.Seats", "Seats must contain at least one seat."));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Domain_rejects_invalid_replacement_before_changing_original_seats(bool empty)
    {
        Hall hall = Hall.Create(EntityId<Hall>.New(), "Hall");
        SeatLayoutService layout = new();
        var original = layout.GenerateRectangularGrid(1, 2, hall.Id, new EntityId<SeatType>(TypeId));
        hall.ApplyLayout(original);
        var invalid = empty ? [] : layout.GenerateRectangularGrid(1, 1,
            EntityId<Hall>.New(), new EntityId<SeatType>(TypeId));

        Action replace = () => hall.ReplaceLayout(invalid);
        replace.Should().Throw<DomainException>();
        hall.Seats.Should().Equal(original);
        hall.TotalCapacity.Should().Be(2);
    }
}
