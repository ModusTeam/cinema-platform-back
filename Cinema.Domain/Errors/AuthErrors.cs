using Cinema.Domain.Shared;

namespace Cinema.Domain.Errors;

/// <summary>
/// Strongly-typed errors for authentication and authorization failures.
/// </summary>
public static class AuthErrors
{
    public static readonly Error UserNotAuthenticated =
        new("Auth.Required", "An authenticated user is required for this operation.");
}
