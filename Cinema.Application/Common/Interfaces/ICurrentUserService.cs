namespace Cinema.Application.Common.Interfaces;

public interface ICurrentUserService
{
    Guid? UserId { get; }
}