using MediatR;
using Microsoft.EntityFrameworkCore;
using Cinema.Application.Common.Interfaces;

namespace Cinema.Application.AdminLoyalty.Queries
{
    public record GetAdminUsersQuery(int Limit, int Skip, string? TierFilter, string? EmailSearch) : IRequest<AdminUsersListDto>;

    public class GetAdminUsersQueryHandler : IRequestHandler<GetAdminUsersQuery, AdminUsersListDto>
    {
        private readonly IAdminLoyaltyService _adminLoyaltyService;
        private readonly IApplicationDbContext _context; 

        public GetAdminUsersQueryHandler(IAdminLoyaltyService adminLoyaltyService, IApplicationDbContext context)
        {
            _adminLoyaltyService = adminLoyaltyService;
            _context = context;
        }

        public async Task<AdminUsersListDto> Handle(GetAdminUsersQuery request, CancellationToken ct)
        {
            List<Guid>? filteredUserIds = null;

            if (!string.IsNullOrWhiteSpace(request.EmailSearch))
            {
                var searchLower = request.EmailSearch.ToLower();
                
                filteredUserIds = await _context.Users
                    .Where(u => (u.Email != null && u.Email.ToLower().Contains(searchLower)) || (u.UserName != null && u.UserName.ToLower().Contains(searchLower)))
                    .Select(u => u.Id)
                    .ToListAsync(ct);

                if (!filteredUserIds.Any())
                {
                    return new AdminUsersListDto(new List<AdminUserProfileDto>(), 0);
                }
            }

            var loyaltyData = await _adminLoyaltyService.GetUsersAsync(request.Limit, request.Skip, request.TierFilter, filteredUserIds, ct);

            if (!loyaltyData.Profiles.Any())
            {
                return new AdminUsersListDto(new List<AdminUserProfileDto>(), loyaltyData.TotalCount);
            }

            var resultUserIds = loyaltyData.Profiles.Select(p => p.UserId).ToList();

            var usersFromDb = await _context.Users
                .Where(u => resultUserIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Email, u.UserName })
                .ToListAsync(ct);

            var enrichedProfiles = loyaltyData.Profiles.Select(lp => 
            {
                var dbUser = usersFromDb.FirstOrDefault(u => u.Id == lp.UserId);
                return new AdminUserProfileDto(
                    lp.UserId, 
                    dbUser?.Email ?? "Unknown", 
                    dbUser?.UserName ?? "Unknown", 
                    lp.Tier, 
                    lp.Balance, 
                    lp.LifetimePoints
                );
            });

            return new AdminUsersListDto(enrichedProfiles, loyaltyData.TotalCount);
        }
    }
}