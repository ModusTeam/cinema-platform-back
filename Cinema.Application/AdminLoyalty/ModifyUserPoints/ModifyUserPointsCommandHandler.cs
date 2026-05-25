using MediatR;
using Cinema.Application.Common.Interfaces;

namespace Cinema.Application.AdminLoyalty.Commands
{
    public record ModifyUserPointsCommand(Guid UserId, string AdminId, int Points, string Reason) 
        : IRequest<AdminModifyPointsDto>;

    public class ModifyUserPointsCommandHandler : IRequestHandler<ModifyUserPointsCommand, AdminModifyPointsDto>
    {
        private readonly IAdminLoyaltyService _adminLoyaltyService;

        public ModifyUserPointsCommandHandler(IAdminLoyaltyService adminLoyaltyService)
        {
            _adminLoyaltyService = adminLoyaltyService;
        }

        public async Task<AdminModifyPointsDto> Handle(ModifyUserPointsCommand request, CancellationToken cancellationToken)
        {
            return await _adminLoyaltyService.ModifyPointsAsync(
                request.UserId, request.AdminId, request.Points, request.Reason, cancellationToken);
        }
    }
}