using Cinema.Domain.Shared;

namespace Cinema.Domain.Errors;

/// <summary>
/// Strongly-typed errors for user-related operations.
/// </summary>
public static class UserErrors
{
    public static readonly Error NotFound =
        new("User.NotFound", "The requested user was not found.");

    public static readonly Error DateOfBirthAlreadySet =
        new("User.DateOfBirthAlreadySet", "Date of birth has already been set and cannot be changed.");

    public static readonly Error UpdateFailed =
        new("User.UpdateFailed", "Failed to update the user record.");
}
