using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Account.Commands.SetMyDateOfBirth;

public record SetMyDateOfBirthCommand(DateTime DateOfBirth) : IRequest<Result<SetMyDateOfBirthResponse>>;

public record SetMyDateOfBirthResponse(DateTime DateOfBirth);
