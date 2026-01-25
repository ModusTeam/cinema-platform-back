using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Commands.CreateSession;

public class CreateSessionCommandHandler : IRequestHandler<CreateSessionCommand, Result<Guid>>
{
    private readonly IApplicationDbContext _context;

    public CreateSessionCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Result<Guid>> Handle(CreateSessionCommand request, CancellationToken cancellationToken)
    {
        var hallId = new EntityId<Hall>(request.HallId);
        var movieId = new EntityId<Movie>(request.MovieId);
        var pricingId = new EntityId<Pricing>(request.PricingId);

        var movie = await _context.Movies.FirstOrDefaultAsync(m => m.Id == movieId, cancellationToken);
        if (movie == null)
            return Result<Guid>.Failure(new Error("Session.MovieNotFound", "Movie not found."));
        
        var pricingExists = await _context.Pricings.AnyAsync(p => p.Id == pricingId, cancellationToken);
        if (!pricingExists)
            return Result<Guid>.Failure(new Error("Session.InvalidPricing", "Pricing policy not found."));
        
        var endTime = request.StartTime.AddMinutes(movie.DurationMinutes + 10);
        
        var isOverlapping = await _context.Sessions
            .AnyAsync(s =>
                s.HallId == hallId &&
                s.Status != SessionStatus.Cancelled &&
                s.StartTime < endTime && 
                s.EndTime > request.StartTime,
                cancellationToken);

        if (isOverlapping)
        {
            return Result<Guid>.Failure(new Error("Session.Overlap", "This time slot is already booked in this hall."));
        }
        
        var sessionId = new EntityId<Session>(Guid.NewGuid());
        var session = Session.New(
            sessionId,
            request.StartTime,
            endTime,
            SessionStatus.Scheduled, 
            movieId,
            hallId,
            pricingId
        );

        _context.Sessions.Add(session);
        await _context.SaveChangesAsync(cancellationToken);

        return Result<Guid>.Success(sessionId.Value);
    }
}