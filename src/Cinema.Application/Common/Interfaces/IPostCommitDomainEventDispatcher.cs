namespace Cinema.Application.Common.Interfaces;

public interface IPostCommitDomainEventDispatcher
{
    Task DispatchAfterCommitAsync(CancellationToken cancellationToken);
}
