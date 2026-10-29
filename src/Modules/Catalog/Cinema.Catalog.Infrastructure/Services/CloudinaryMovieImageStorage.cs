using System.Net;
using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Models.MovieImages;
using Cinema.Catalog.Application.Movies.Images;
using Cinema.Catalog.Infrastructure.Options;
using Cinema.Domain.Shared;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinema.Catalog.Infrastructure.Services;

public sealed class CloudinaryMovieImageStorage(
    IOptions<CloudinaryOptions> options,
    ILogger<CloudinaryMovieImageStorage> logger,
    ICloudinaryUploadApi? uploadApi = null) : IMovieImageStorage
{
    public async Task<Result<MovieImageUploadResult>> UploadAsync(MovieImageUpload image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CloudinaryOptions settings;
        try
        {
            settings = options.Value;
        }
        catch (OptionsValidationException)
        {
            // Validation exceptions can contain configuration values; never log them.
            logger.LogWarning("Movie image storage configuration is missing or invalid.");
            return Result.Failure<MovieImageUploadResult>(MovieImageErrors.Unavailable);
        }

        string folder = image.Kind switch
        {
            MovieImageKind.Poster => "cinema-platform/movies/posters",
            MovieImageKind.Backdrop => "cinema-platform/movies/backdrops",
            _ => string.Empty
        };
        if (folder.Length == 0)
            return Result.Failure<MovieImageUploadResult>(MovieImageErrors.InvalidKind);

        try
        {
            ICloudinaryUploadApi client = uploadApi ?? CreateClient(settings);
            ImageUploadParams parameters = new()
            {
                File = new FileDescription($"image.{image.Format}", image.Content),
                Folder = folder,
                PublicId = Guid.NewGuid().ToString("N"),
                Overwrite = false,
                UseFilename = false,
                AllowedFormats = ["jpg", "jpeg", "png", "webp"]
            };
            ImageUploadResult response = await client.UploadAsync(parameters, ct);
            ct.ThrowIfCancellationRequested();

            if (response.Error is not null || (int)response.StatusCode is < 200 or >= 300)
            {
                logger.LogWarning("Movie image upload failed. Provider HTTP status: {StatusCode}", (int)response.StatusCode);
                Cinema.Domain.Shared.Error error = response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => MovieImageErrors.InvalidContent,
                    HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => MovieImageErrors.Timeout,
                    _ => MovieImageErrors.ProviderFailure
                };
                return Result.Failure<MovieImageUploadResult>(error);
            }

            if (response.SecureUrl is not { IsAbsoluteUri: true, Scheme: "https" } ||
                string.IsNullOrWhiteSpace(response.PublicId) || response.Width <= 0 || response.Height <= 0 ||
                response.Bytes <= 0 || response.Format is not ("jpg" or "jpeg" or "png" or "webp"))
            {
                logger.LogWarning("Movie image storage returned incomplete or insecure metadata.");
                return Result.Failure<MovieImageUploadResult>(MovieImageErrors.ProviderFailure);
            }

            return Result.Success(new MovieImageUploadResult(response.SecureUrl.AbsoluteUri,
                response.PublicId, response.AssetId, response.Width, response.Height, response.Format, response.Bytes));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Movie image storage request timed out.");
            return Result.Failure<MovieImageUploadResult>(MovieImageErrors.Timeout);
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Movie image storage network request failed.");
            return Result.Failure<MovieImageUploadResult>(MovieImageErrors.ProviderFailure);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Movie image storage request timed out.");
            return Result.Failure<MovieImageUploadResult>(MovieImageErrors.Timeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Provider exception messages may contain signed request details.
            logger.LogWarning("Movie image storage request failed. Exception type: {ExceptionType}", exception.GetType().Name);
            return Result.Failure<MovieImageUploadResult>(MovieImageErrors.ProviderFailure);
        }
    }

    private static Cloudinary CreateClient(CloudinaryOptions settings)
    {
        Cloudinary client = new(new Account(settings.CloudName, settings.ApiKey, settings.ApiSecret));
        client.Api.Secure = true;
        client.Api.Timeout = 30_000;
        return client;
    }
}
