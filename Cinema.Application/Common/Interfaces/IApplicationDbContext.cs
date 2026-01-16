using System.Data;

namespace Cinema.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    Task<IDbTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

}