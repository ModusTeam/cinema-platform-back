namespace Cinema.Application.Common.Interfaces
{
    public interface IAdminLoyaltyService
    {
        Task<AdminUserBalanceDto> GetUserBalanceAsync(Guid userId, CancellationToken ct);
        Task<AdminTransactionHistoryDto> GetTransactionHistoryAsync(Guid userId, int limit, int skip, CancellationToken ct);
        Task<AdminModifyPointsDto> ModifyPointsAsync(Guid userId, string adminId, int points, string reason, CancellationToken ct);
    }

    public record AdminUserBalanceDto(string Tier, int Balance, int LifetimePoints);
    
    public record AdminTransactionHistoryDto(IEnumerable<AdminTransactionDto> Transactions);
    
    public record AdminTransactionDto(string Id, string Type, int Points, int BalanceAfter, string OrderId, string Description, string CreatedAt);
    
    public record AdminModifyPointsDto(Guid UserId, string Tier, int Balance, bool Success);
}