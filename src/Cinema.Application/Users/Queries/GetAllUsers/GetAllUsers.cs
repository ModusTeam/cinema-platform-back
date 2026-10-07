using Cinema.Application.Common.Interfaces;
using Cinema.Application.Users.Dtos;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Users.Queries.GetAllUsers;

public record GetAllUsersQuery : IRequest<Result<List<UserDto>>>;

public class GetAllUsersQueryHandler(IUserService userService) 
    : IRequestHandler<GetAllUsersQuery, Result<List<UserDto>>>
{
    public async Task<Result<List<UserDto>>> Handle(GetAllUsersQuery request, CancellationToken ct)
    {
        return await userService.GetAllAsync(ct);
    }
}