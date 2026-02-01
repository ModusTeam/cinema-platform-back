using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Account.Commands.ChangePassword;

public record ChangePasswordCommand(string OldPassword, string NewPassword, string ConfirmNewPassword) : IRequest<Result>;