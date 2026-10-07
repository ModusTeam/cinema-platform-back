using Cinema.Application.Users.Dtos;
using Cinema.Domain.Shared;

namespace Cinema.Application.Common.Interfaces;

public interface IUserService
{
    Task<Result<List<UserDto>>> GetAllAsync(CancellationToken ct = default);
    Task<Result> ChangeRoleAsync(Guid userId, string newRole);
}