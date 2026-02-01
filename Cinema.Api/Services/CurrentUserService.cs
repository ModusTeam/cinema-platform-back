using System.Security.Claims;
using Cinema.Application.Common.Interfaces;

namespace Cinema.Api.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? UserId
    {
        get
        {
            var idClaim = _httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier);
            if (idClaim == null) return null;
            
            return Guid.TryParse(idClaim.Value, out var userId) ? userId : null;
        }
    }
}