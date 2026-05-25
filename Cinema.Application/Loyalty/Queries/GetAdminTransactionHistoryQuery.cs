using MediatR;
using Cinema.Application.Common.Interfaces;

namespace Cinema.Application.AdminLoyalty.Queries
{
    public record GetAdminTransactionHistoryQuery(Guid UserId, int Limit, int Skip) 
        : IRequest<AdminTransactionHistoryDto>;

    public class GetAdminTransactionHistoryQueryHandler : IRequestHandler<GetAdminTransactionHistoryQuery, AdminTransactionHistoryDto>
    {
        private readonly IAdminLoyaltyService _adminLoyaltyService;

        public GetAdminTransactionHistoryQueryHandler(IAdminLoyaltyService adminLoyaltyService)
        {
            _adminLoyaltyService = adminLoyaltyService;
        }

        public async Task<AdminTransactionHistoryDto> Handle(GetAdminTransactionHistoryQuery request, CancellationToken cancellationToken)
        {
            return await _adminLoyaltyService.GetTransactionHistoryAsync(request.UserId, request.Limit, request.Skip, cancellationToken);
        }
    }
}