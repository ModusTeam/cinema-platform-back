using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Cinema.Application.Common.Interfaces;

namespace Cinema.Application.AdminLoyalty.GrantVipStatus
{
    public record GrantVipStatusCommand(Guid UserId, string AdminId, string Reason) : IRequest<AdminGrantVipDto>;

    public class GrantVipStatusCommandHandler : IRequestHandler<GrantVipStatusCommand, AdminGrantVipDto>
    {
        private readonly IAdminLoyaltyService _adminLoyaltyService;

        public GrantVipStatusCommandHandler(IAdminLoyaltyService adminLoyaltyService) => _adminLoyaltyService = adminLoyaltyService;

        public async Task<AdminGrantVipDto> Handle(GrantVipStatusCommand request, CancellationToken ct)
        {
            return await _adminLoyaltyService.GrantVipStatusAsync(request.UserId, request.AdminId, request.Reason, ct);
        }
    }
}