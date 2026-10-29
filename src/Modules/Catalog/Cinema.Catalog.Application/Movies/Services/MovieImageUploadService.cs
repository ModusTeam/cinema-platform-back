using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Models.MovieImages;
using Cinema.Catalog.Application.Movies.Images;
using Cinema.Domain.Shared;

namespace Cinema.Catalog.Application.Movies.Services;

public sealed class MovieImageUploadService(IMovieImageStorage storage)
{
    public const long MaxFileBytes = 10 * 1024 * 1024;

    public async Task<Result<MovieImageUploadResult>> UploadAsync(Stream? content, string? kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        MovieImageKind imageKind;
        switch (kind)
        {
            case "poster": imageKind = MovieImageKind.Poster; break;
            case "backdrop": imageKind = MovieImageKind.Backdrop; break;
            default: return Result.Failure<MovieImageUploadResult>(MovieImageErrors.InvalidKind);
        }

        // ASP.NET's bounded multipart buffering supplies a seekable file stream.
        // Read a fixed-size signature and rewind, without copying the whole image.
        if (content is null || !content.CanRead || !content.CanSeek || content.Length == 0)
            return Result.Failure<MovieImageUploadResult>(MovieImageErrors.FileRequired);
        if (content.Length > MaxFileBytes)
            return Result.Failure<MovieImageUploadResult>(MovieImageErrors.TooLarge);

        content.Position = 0;
        byte[] header = new byte[16];
        int read = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken: ct);
        content.Position = 0;
        string? format = DetectFormat(header.AsSpan(0, read));
        if (format is null)
            return Result.Failure<MovieImageUploadResult>(MovieImageErrors.InvalidContent);

        return await storage.UploadAsync(new MovieImageUpload(content, imageKind, format), ct);
    }

    private static string? DetectFormat(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith<byte>([0xFF, 0xD8, 0xFF])) return "jpg";
        if (header.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "png";
        if (header.Length >= 16 && header[..4].SequenceEqual("RIFF"u8) &&
            header[8..12].SequenceEqual("WEBP"u8) &&
            (header[12..16].SequenceEqual("VP8 "u8) ||
             header[12..16].SequenceEqual("VP8L"u8) ||
             header[12..16].SequenceEqual("VP8X"u8))) return "webp";
        return null;
    }
}
