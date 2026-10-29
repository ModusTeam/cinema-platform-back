using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Models.MovieImages;
using Cinema.Catalog.Application.Movies.Images;
using Cinema.Catalog.Application.Movies.Services;
using Cinema.Domain.Shared;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Cinema.CatalogTests;

public class MovieImageUploadTests
{
    internal static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZ1cAAAAASUVORK5CYII=");

    internal static readonly MovieImageUploadResult Metadata = new(
        "https://res.cloudinary.com/test/image/upload/cinema-platform/movies/posters/test.png",
        "cinema-platform/movies/posters/test", "asset-id", 1, 1, "png", Png.Length);

    [Theory]
    [InlineData("poster", MovieImageKind.Poster)]
    [InlineData("backdrop", MovieImageKind.Backdrop)]
    public async Task Valid_image_is_rewound_and_forwarded_with_kind_and_cancellation(string kind, MovieImageKind expected)
    {
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        using MemoryStream content = new(Png);
        storage.UploadAsync(Arg.Any<MovieImageUpload>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            MovieImageUpload image = call.Arg<MovieImageUpload>();
            image.Kind.Should().Be(expected);
            image.Format.Should().Be("png");
            image.Content.Should().BeSameAs(content);
            image.Content.Position.Should().Be(0);
            return Result.Success(Metadata);
        });

        var result = await new MovieImageUploadService(storage).UploadAsync(content, kind, TestContext.Current.CancellationToken);

        result.Value.Should().Be(Metadata);
        await storage.Received(1).UploadAsync(Arg.Any<MovieImageUpload>(), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("video")]
    [InlineData("0")]
    [InlineData("../../folder")]
    public async Task Invalid_kind_never_calls_storage(string? kind)
    {
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        using MemoryStream content = new(Png);
        var result = await new MovieImageUploadService(storage).UploadAsync(content, kind, TestContext.Current.CancellationToken);
        result.Error.Should().Be(MovieImageErrors.InvalidKind);
        storage.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_and_empty_files_are_rejected()
    {
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        MovieImageUploadService service = new(storage);
        using MemoryStream empty = new();
        (await service.UploadAsync(null, "poster", TestContext.Current.CancellationToken)).Error.Should().Be(MovieImageErrors.FileRequired);
        (await service.UploadAsync(empty, "poster", TestContext.Current.CancellationToken)).Error.Should().Be(MovieImageErrors.FileRequired);
        storage.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Oversized_file_is_rejected_before_reading()
    {
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        Stream content = Substitute.For<Stream>();
        content.CanRead.Returns(true);
        content.CanSeek.Returns(true);
        content.Length.Returns(MovieImageUploadService.MaxFileBytes + 1);
        var result = await new MovieImageUploadService(storage).UploadAsync(content, "poster", TestContext.Current.CancellationToken);
        result.Error.Should().Be(MovieImageErrors.TooLarge);
        storage.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("<html>not an image</html>")]
    [InlineData("%PDF-1.7")]
    [InlineData("GIF89a")]
    [InlineData("RIFF0000AVI ")]
    [InlineData("RIFF0000WEBPxxxx")]
    [InlineData("arbitrary binary")]
    public async Task Unsupported_content_is_rejected(string body)
    {
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        using MemoryStream content = new(System.Text.Encoding.UTF8.GetBytes(body));
        var result = await new MovieImageUploadService(storage).UploadAsync(content, "poster", TestContext.Current.CancellationToken);
        result.Error.Should().Be(MovieImageErrors.InvalidContent);
        storage.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("/9j/4AAQSkZJRgABAQAAAQABAAD/2Q==", "jpg")]
    [InlineData("UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==", "webp")]
    public async Task Accepted_signatures_are_detected_from_bytes(string encoded, string format)
    {
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        storage.UploadAsync(Arg.Any<MovieImageUpload>(), Arg.Any<CancellationToken>()).Returns(Result.Success(Metadata));
        using MemoryStream content = new(Convert.FromBase64String(encoded));
        (await new MovieImageUploadService(storage).UploadAsync(content, "poster", TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
        await storage.Received(1).UploadAsync(Arg.Is<MovieImageUpload>(image => image.Format == format), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelled_read_does_not_call_storage()
    {
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        using MemoryStream content = new(Png);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Func<Task> upload = () => new MovieImageUploadService(storage).UploadAsync(content, "poster", cancellation.Token);
        await upload.Should().ThrowAsync<OperationCanceledException>();
        storage.ReceivedCalls().Should().BeEmpty();
    }
}
