namespace Cinema.Application.Common.Interfaces
{
    public interface IAdminLoyaltyService
    {
        Task<AdminUserBalanceDto> GetUserBalanceAsync(Guid userId, CancellationToken ct);
        Task<AdminTransactionHistoryDto> GetTransactionHistoryAsync(Guid userId, int limit, int skip, CancellationToken ct);
        Task<AdminModifyPointsDto> ModifyPointsAsync(Guid userId, string adminId, int points, string reason, CancellationToken ct);
        Task<AdminUsersListDto> GetUsersAsync(int limit, int skip, string? tierFilter, IEnumerable<Guid>? userIds, CancellationToken ct);
        Task<AdminGrantVipDto> GrantVipStatusAsync(Guid userId, string adminId, string reason, CancellationToken ct);
    }

    public record AdminUserBalanceDto(string Tier, int Balance, int LifetimePoints);
    
    public record AdminTransactionHistoryDto(IEnumerable<AdminTransactionDto> Transactions);
    
    public record AdminTransactionDto(string Id, string Type, int Points, int BalanceAfter, string OrderId, string Description, string CreatedAt);
    
    public record AdminModifyPointsDto(Guid UserId, string Tier, int Balance, bool Success);
    public record AdminUsersListDto(IEnumerable<AdminUserProfileDto> Profiles, int TotalCount);
    public record AdminUserProfileDto(Guid UserId, string Email, string UserName, string Tier, int Balance, int LifetimePoints);
    public record AdminGrantVipDto(Guid UserId, string NewTier, bool Success);
}