using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Account.Queries.GetProfile;

public record GetProfileQuery : IRequest<Result<UserProfileDto>>;