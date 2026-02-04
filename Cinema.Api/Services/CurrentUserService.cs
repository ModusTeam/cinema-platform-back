using System.Security.Claims;
using Cinema.Application.Common.Interfaces;

namespace Cinema.Api.Services;

public class CurrentUserService(IHttpContextAccessor httpContextAccessor) : ICurrentUserService
{
    public Guid? UserId
    {
        get
        {
            var id = httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            return id == null ? null : Guid.Parse(id);
        }
    }

    public bool IsInRole(string role)
    {
        return httpContextAccessor.HttpContext?.User?.IsInRole(role) ?? false;
    }
}