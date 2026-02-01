using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Account.Commands.UpdateProfile;

public record UpdateProfileCommand(string FirstName, string LastName) : IRequest<Result>;