using System.Net;
using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Models.MovieImages;
using Cinema.Catalog.Application.Movies.Images;
using Cinema.Catalog.Infrastructure;
using Cinema.Catalog.Infrastructure.Options;
using Cinema.Catalog.Infrastructure.Services;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Cinema.CatalogTests;

public class CloudinaryMovieImageStorageTests
{
    private readonly ICloudinaryUploadApi _sdk = Substitute.For<ICloudinaryUploadApi>();

    private CloudinaryMovieImageStorage CreateStorage() => new(
        Options.Create(new CloudinaryOptions { CloudName = "test", ApiKey = "123", ApiSecret = "test-secret" }),
        NullLogger<CloudinaryMovieImageStorage>.Instance, _sdk);

    private static ImageUploadResult Success() => new()
    {
        StatusCode = HttpStatusCode.OK,
        SecureUrl = new Uri(MovieImageUploadTests.Metadata.SecureUrl),
        PublicId = MovieImageUploadTests.Metadata.PublicId,
        AssetId = "asset-id",
        Width = 1,
        Height = 1,
        Format = "png",
        Bytes = MovieImageUploadTests.Png.Length
    };

    [Theory]
    [InlineData(MovieImageKind.Poster, "cinema-platform/movies/posters")]
    [InlineData(MovieImageKind.Backdrop, "cinema-platform/movies/backdrops")]
    public async Task Upload_uses_image_only_scoped_unique_non_overwriting_parameters_and_real_metadata(MovieImageKind kind, string folder)
    {
        List<ImageUploadParams> uploads = [];
        _sdk.UploadAsync(Arg.Any<ImageUploadParams>(), Arg.Any<CancellationToken?>()).Returns(call =>
        {
            uploads.Add(call.Arg<ImageUploadParams>());
            return Success();
        });
        using MemoryStream content = new(MovieImageUploadTests.Png);
        MovieImageUpload image = new(content, kind, "png");
        CloudinaryMovieImageStorage storage = CreateStorage();
        var result = await storage.UploadAsync(image, TestContext.Current.CancellationToken);
        await storage.UploadAsync(image, TestContext.Current.CancellationToken);

        result.Value.Should().Be(MovieImageUploadTests.Metadata);
        uploads.Should().HaveCount(2);
        uploads[0].Folder.Should().Be(folder);
        uploads[0].Overwrite.Should().BeFalse();
        uploads[0].UseFilename.Should().BeFalse();
        uploads[0].AllowedFormats.Should().BeEquivalentTo("jpg", "jpeg", "png", "webp");
        Guid.TryParseExact(uploads[0].PublicId, "N", out _).Should().BeTrue();
        uploads[0].PublicId.Should().NotBe(uploads[1].PublicId);
        uploads[0].Transformation.Should().BeNull();
        await _sdk.Received(2).UploadAsync(Arg.Any<ImageUploadParams>(), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "MovieImage.ProviderFailure")]
    [InlineData(HttpStatusCode.Unauthorized, "MovieImage.ProviderFailure")]
    [InlineData(HttpStatusCode.Forbidden, "MovieImage.ProviderFailure")]
    [InlineData(HttpStatusCode.BadRequest, "MovieImage.Validation.Content")]
    [InlineData(HttpStatusCode.InternalServerError, "MovieImage.ProviderFailure")]
    [InlineData(HttpStatusCode.RequestTimeout, "MovieImage.Timeout")]
    [InlineData(HttpStatusCode.GatewayTimeout, "MovieImage.Timeout")]
    public async Task Provider_error_is_checked_even_with_success_status_and_never_disclosed(HttpStatusCode status, string code)
    {
        _sdk.UploadAsync(Arg.Any<ImageUploadParams>(), Arg.Any<CancellationToken?>()).Returns(new ImageUploadResult
        {
            StatusCode = status,
            Error = new CloudinaryDotNet.Actions.Error { Message = "sensitive-provider-details" }
        });
        using MemoryStream content = new(MovieImageUploadTests.Png);
        var result = await CreateStorage().UploadAsync(new(content, MovieImageKind.Poster, "png"), TestContext.Current.CancellationToken);
        result.Error.Code.Should().Be(code);
        result.Error.Description.Should().NotContain("sensitive-provider-details");
    }

    [Theory]
    [InlineData("network", "MovieImage.ProviderFailure")]
    [InlineData("timeout", "MovieImage.Timeout")]
    [InlineData("explicit-timeout", "MovieImage.Timeout")]
    [InlineData("unexpected", "MovieImage.ProviderFailure")]
    public async Task Provider_exceptions_are_controlled(string failure, string code)
    {
        Exception exception = failure switch
        {
            "network" => new HttpRequestException("sensitive signed request"),
            "timeout" => new TaskCanceledException("sensitive signed request"),
            "explicit-timeout" => new TimeoutException("sensitive signed request"),
            _ => new InvalidOperationException("sensitive signed request")
        };
        _sdk.UploadAsync(Arg.Any<ImageUploadParams>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromException<ImageUploadResult>(exception));
        using MemoryStream content = new(MovieImageUploadTests.Png);
        var result = await CreateStorage().UploadAsync(new(content, MovieImageKind.Poster, "png"), TestContext.Current.CancellationToken);
        result.Error.Code.Should().Be(code);
        result.Error.Description.Should().NotContain("sensitive");
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        using CancellationTokenSource cancellation = new();
        _sdk.UploadAsync(Arg.Any<ImageUploadParams>(), Arg.Any<CancellationToken?>()).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<ImageUploadResult>(cancellation.Token);
        });
        using MemoryStream content = new(MovieImageUploadTests.Png);
        Func<Task> upload = () => CreateStorage().UploadAsync(new(content, MovieImageKind.Poster, "png"), cancellation.Token);
        await upload.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("http://res.cloudinary.com/test/image/upload/image.png", true)]
    [InlineData("https://res.cloudinary.com/test/image/upload/image.png", false)]
    public async Task Insecure_or_incomplete_metadata_is_never_successful(string url, bool complete)
    {
        ImageUploadResult response = Success();
        response.SecureUrl = new Uri(url);
        if (!complete) response.PublicId = "";
        _sdk.UploadAsync(Arg.Any<ImageUploadParams>(), Arg.Any<CancellationToken?>()).Returns(response);
        using MemoryStream content = new(MovieImageUploadTests.Png);
        (await CreateStorage().UploadAsync(new(content, MovieImageKind.Poster, "png"), TestContext.Current.CancellationToken))
            .Error.Should().Be(MovieImageErrors.ProviderFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_partial_configuration_resolves_storage_but_upload_is_unavailable(bool partial)
    {
        Dictionary<string, string?> values = partial ? new() { ["Cloudinary:CloudName"] = "test" } : new();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using ServiceProvider services = new ServiceCollection().AddLogging()
            .AddCatalogInfrastructure(configuration).BuildServiceProvider();
        using IServiceScope scope = services.CreateScope();
        IMovieImageStorage storage = scope.ServiceProvider.GetRequiredService<IMovieImageStorage>();
        storage.Should().BeOfType<CloudinaryMovieImageStorage>();
        using MemoryStream content = new(MovieImageUploadTests.Png);
        (await storage.UploadAsync(new(content, MovieImageKind.Poster, "png"), TestContext.Current.CancellationToken))
            .Error.Should().Be(MovieImageErrors.Unavailable);
    }
}
