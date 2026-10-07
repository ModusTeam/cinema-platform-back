using MediatR;
using Cinema.Application.Common.Interfaces;

namespace Cinema.Application.AdminLoyalty.Queries
{
    public record GetAdminUserBalanceQuery(Guid UserId) : IRequest<AdminUserBalanceDto>;

    public class GetAdminUserBalanceQueryHandler : IRequestHandler<GetAdminUserBalanceQuery, AdminUserBalanceDto>
    {
        private readonly IAdminLoyaltyService _adminLoyaltyService;

        public GetAdminUserBalanceQueryHandler(IAdminLoyaltyService adminLoyaltyService)
        {
            _adminLoyaltyService = adminLoyaltyService;
        }

        public async Task<AdminUserBalanceDto> Handle(GetAdminUserBalanceQuery request, CancellationToken cancellationToken)
        {
            return await _adminLoyaltyService.GetUserBalanceAsync(request.UserId, cancellationToken);
        }
    }
}