namespace Cinema.Application.Account.Queries.GetProfile;

public record UserProfileDto(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    DateTime? DateOfBirth,
    int LoyaltyPoints,
    string LoyaltyTier
);