using Cinema.Domain.Shared;

namespace Cinema.Catalog.Application.Movies.Images;

public static class MovieImageErrors
{
    public static readonly Error InvalidReference = new("MovieImage.Validation.Reference", "The image must be an existing application-owned upload in the correct movie folder, with its original secure URL and public ID.");
    public static readonly Error FileRequired = new("MovieImage.Validation.File", "A non-empty image file is required.");
    public static readonly Error TooLarge = new("MovieImage.TooLarge", "The image must not exceed 10 MB (10,485,760 bytes).");
    public static readonly Error InvalidKind = new("MovieImage.Validation.Kind", "Image kind must be poster or backdrop.");
    public static readonly Error InvalidContent = new("MovieImage.Validation.Content", "Only JPEG, PNG and WebP image content is accepted.");
    public static readonly Error Unavailable = new("MovieImage.Unavailable", "Movie image storage is not configured. Contact the administrator.");
    public static readonly Error ProviderFailure = new("MovieImage.ProviderFailure", "Image storage could not complete the upload. Try again later.");
    public static readonly Error Timeout = new("MovieImage.Timeout", "Image storage timed out. Try again later.");
}
