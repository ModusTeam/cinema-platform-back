namespace Cinema.Application.Users.Dtos;

public record UserDto(Guid Id, string Email, string FirstName, string LastName, string Role);