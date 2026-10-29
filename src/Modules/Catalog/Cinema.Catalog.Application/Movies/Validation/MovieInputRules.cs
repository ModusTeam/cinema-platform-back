using Cinema.Catalog.Application.Common.Models.MovieImages;
using Cinema.Catalog.Application.Movies.Models;

namespace Cinema.Catalog.Application.Movies.Validation;

internal static class MovieInputRules
{
    public static bool ValidGenreIds(IReadOnlyList<Guid>? ids) => ids is null ||
        (ids.Count <= 50 && ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Count);

    public static bool ValidRating(decimal rating) => rating is >= 0 and <= 10 && decimal.Round(rating, 2) == rating;

    public static bool ValidAgeRestriction(string? age) => age is null or "0+" or "7+" or "12+" or "16+" or "18+";

    public static bool ValidTrailerUrl(string? url) => url is null || ValidHttpsUrl(url);

    public static bool ValidHttpsUrl(string? url) => !string.IsNullOrWhiteSpace(url) && url.Length <= 500 &&
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    public static bool ValidImageReference(MovieImageReference? image) => image is null ||
        (ValidHttpsUrl(image.SecureUrl) && !string.IsNullOrWhiteSpace(image.PublicId) && image.PublicId.Length <= 255);

    public static bool ValidCast(IReadOnlyList<ManualCastMember>? cast) => cast is null ||
        (cast.Count <= 50 && cast.All(member => member is not null &&
            !string.IsNullOrWhiteSpace(member.Name) && member.Name.Length <= 200 &&
            member.Role is not { Length: > 200 }) &&
         cast.Select(member => (member.Name.Trim().ToUpperInvariant(), member.Role?.Trim().ToUpperInvariant()))
             .Distinct().Count() == cast.Count);
}
