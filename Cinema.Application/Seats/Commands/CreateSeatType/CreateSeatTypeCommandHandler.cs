using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Seats.Commands.CreateSeatType;

public class CreateSeatTypeCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<CreateSeatTypeCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateSeatTypeCommand request, CancellationToken ct)
    {
        var exists = await context.SeatTypes.AnyAsync(x => x.Name == request.Name, ct);
        if (exists)
            return Result.Failure<Guid>(new Error("SeatType.Exists", "Seat type with this name already exists."));

        var id = new EntityId<SeatType>(Guid.NewGuid());
        var seatType = SeatType.New(id, request.Name, request.Description);

        context.SeatTypes.Add(seatType);
        await context.SaveChangesAsync(ct);

        return Result.Success(id.Value);
    }
}