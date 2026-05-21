namespace Cinema.Application.Common.Constants;

/// <summary>
/// Single source of truth for Identity role names.
/// Use these constants everywhere instead of raw string literals
/// to prevent 403 errors caused by casing mismatches.
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string User  = "User";
}
