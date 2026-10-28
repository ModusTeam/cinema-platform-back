using Cinema.Domain.Entities;
using Cinema.Domain.Shared;

namespace Cinema.Application.Common.Interfaces;

public interface ICustomerFactsWriter
{
    Task<Result> ExecuteAsync(Guid userId, Func<CancellationToken, Task<Result>> mutation, CancellationToken ct);
    Task StageAsync(User user, Guid eventId, string kind, CancellationToken ct);
}
