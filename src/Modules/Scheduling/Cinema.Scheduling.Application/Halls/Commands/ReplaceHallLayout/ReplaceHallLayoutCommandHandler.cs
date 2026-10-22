using Cinema.Application.Common.Interfaces;
using Cinema.Application.Services;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Services;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Halls.Commands.ReplaceHallLayout;

public class ReplaceHallLayoutCommandHandler(IApplicationDbContext context,
    SeatLayoutService layoutService, HallLayoutEditingService editingService)
    : IRequestHandler<ReplaceHallLayoutCommand, Result>
{
    public Task<Result> Handle(ReplaceHallLayoutCommand request, CancellationToken ct)
        => editingService.EditAsync(new EntityId<Hall>(request.HallId), async (hall, token) =>
        {
            var typeIds = request.Seats.Select(s => new EntityId<SeatType>(s.SeatTypeId))
                .Distinct().ToList();
            int existingCount = await context.SeatTypes.CountAsync(t => typeIds.Contains(t.Id), token);
            if (existingCount != typeIds.Count)
                return Result.Failure(new Error("SeatType.NotFound", "One or more provided seat types do not exist."));

            var replacement = layoutService.GenerateSparseLayout(
                request.Seats.Select(s => (s.GridX, s.GridY, new EntityId<SeatType>(s.SeatTypeId))), hall.Id);
            var original = hall.Seats.ToList();
            hall.ReplaceLayout(replacement);
            context.Seats.RemoveRange(original);
            context.Seats.AddRange(replacement);
            return Result.Success();
        }, ct);
}
