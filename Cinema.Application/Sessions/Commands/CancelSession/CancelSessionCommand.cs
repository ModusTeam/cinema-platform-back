using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Exceptions;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Commands.CancelSession;

public record CancelSessionCommand(Guid SessionId) : IRequest<Result>;

public class CancelSessionCommandHandler : IRequestHandler<CancelSessionCommand, Result>
{
    private readonly IApplicationDbContext _context;

    public CancelSessionCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Result> Handle(CancelSessionCommand request, CancellationToken cancellationToken)
    {
        var sessionId = new EntityId<Session>(request.SessionId);
        
        var session = await _context.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session == null)
        {
            return Result.Failure(new Error("Session.NotFound", "Session not found."));
        }

        try 
        {
            session.Cancel(DateTime.UtcNow);
        }
        catch (DomainException ex)
        {
            return Result.Failure(new Error("Session.CancelFailed", ex.Message));
        }

        await _context.SaveChangesAsync(cancellationToken);
        
        return Result.Success();
    }
}