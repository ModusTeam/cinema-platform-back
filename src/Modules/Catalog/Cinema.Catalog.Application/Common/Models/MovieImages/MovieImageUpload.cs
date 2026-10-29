namespace Cinema.Catalog.Application.Common.Models.MovieImages;

public enum MovieImageKind
{
    Poster,
    Backdrop
}

public sealed record MovieImageUpload(Stream Content, MovieImageKind Kind, string Format);

public sealed record MovieImageUploadResult(
    string SecureUrl,
    string PublicId,
    string? AssetId,
    int Width,
    int Height,
    string Format,
    long Bytes);
