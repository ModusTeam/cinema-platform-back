using Cinema.Application.Common.Interfaces;
using Cinema.Application.Services;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Exceptions;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Commands.CreateSession;

public class CreateSessionCommandHandler(
    SessionSchedulingService schedulingService, 
    IApplicationDbContext context) 
    : IRequestHandler<CreateSessionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateSessionCommand request, CancellationToken ct)
    {
        var hallId = new EntityId<Hall>(request.HallId);
        var movieId = new EntityId<Movie>(request.MovieId);
        var pricingId = new EntityId<Pricing>(request.PricingId);

        var hallActive = await context.Halls.AnyAsync(h => h.Id == hallId && h.IsActive, ct);
        if (!hallActive) return Result.Failure<Guid>(new Error("Session.HallNotFound", "Hall not found or inactive."));

        var pricingExists = await context.Pricings.AnyAsync(p => p.Id == pricingId, ct);
        if (!pricingExists) return Result.Failure<Guid>(new Error("Session.PricingNotFound", "Pricing policy not found."));

        try 
        {
            var session = await schedulingService.ScheduleSessionAsync(
                hallId, movieId, pricingId, request.StartTime, 15, ct, request.IsLoyaltyPaymentAllowed
            );

            context.Sessions.Add(session);
            await context.SaveChangesAsync(ct);

            return Result.Success(session.Id.Value);
        }
        catch (DomainException ex)
        {
            return Result.Failure<Guid>(new Error("Session.Validation", ex.Message));
        }
        catch (Exception)
        {
            return Result.Failure<Guid>(new Error("Session.GeneralError", "An unexpected error occurred."));
        }
    }
}