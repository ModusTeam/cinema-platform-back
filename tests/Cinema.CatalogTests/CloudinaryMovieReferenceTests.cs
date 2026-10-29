using System.Net;
using Cinema.Catalog.Application.Common.Models.MovieImages;
using Cinema.Catalog.Application.Movies.Images;
using Cinema.Catalog.Infrastructure.Options;
using Cinema.Catalog.Infrastructure.Services;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Cinema.CatalogTests;

public class CloudinaryMovieReferenceTests
{
    private readonly ICloudinaryAdminApi _sdk = Substitute.For<ICloudinaryAdminApi>();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private CloudinaryMovieImageStorage Storage() => new(
        Options.Create(new CloudinaryOptions { CloudName = "test", ApiKey = "123", ApiSecret = "test-secret" }),
        NullLogger<CloudinaryMovieImageStorage>.Instance, adminApi: _sdk);

    private static GetResourceResult Resource(MovieImageReference reference)
    {
        GetResourceResult result = Newtonsoft.Json.JsonConvert.DeserializeObject<GetResourceResult>(
            """{"resource_type":"image"}""")!;
        result.StatusCode = HttpStatusCode.OK;
        result.PublicId = reference.PublicId;
        result.SecureUrl = reference.SecureUrl;
        result.Type = "upload";
        result.Format = "png";
        result.Width = 1;
        result.Height = 1;
        result.Bytes = 68;
        return result;
    }

    [Theory]
    [InlineData(MovieImageKind.Poster)]
    [InlineData(MovieImageKind.Backdrop)]
    public async Task Checks_authenticated_provider_metadata_in_the_configured_account(MovieImageKind kind)
    {
        MovieImageReference image = kind == MovieImageKind.Poster ? ManualMovieTests.Poster : ManualMovieTests.Backdrop;
        _sdk.GetResourceAsync(Arg.Any<GetResourceParamsBase>(), Arg.Any<CancellationToken?>()).Returns(Resource(image));
        (await Storage().ValidateReferenceAsync(image, kind, Ct)).IsSuccess.Should().BeTrue();
        await _sdk.Received(1).GetResourceAsync(Arg.Is<GetResourceParamsBase>(p =>
            p is GetResourceParams && ((GetResourceParams)p).PublicId == image.PublicId &&
            p.ResourceType == ResourceType.Image && p.Type == "upload"), Ct);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("host")]
    [InlineData("scheme")]
    [InlineData("folder")]
    [InlineData("kind")]
    [InlineData("path")]
    [InlineData("query")]
    [InlineData("credentials")]
    public async Task Untrusted_references_are_rejected_before_contacting_provider(string failure)
    {
        MovieImageReference image = failure switch
        {
            "account" => ManualMovieTests.Poster with { SecureUrl = ManualMovieTests.Poster.SecureUrl.Replace("/test/", "/other/") },
            "host" => ManualMovieTests.Poster with { SecureUrl = ManualMovieTests.Poster.SecureUrl.Replace("res.cloudinary.com", "evil.example") },
            "scheme" => ManualMovieTests.Poster with { SecureUrl = ManualMovieTests.Poster.SecureUrl.Replace("https:", "http:") },
            "folder" => ManualMovieTests.Poster with { PublicId = "unrelated/image" },
            "kind" => ManualMovieTests.Backdrop,
            "path" => ManualMovieTests.Poster with { PublicId = ManualMovieTests.Poster.PublicId + "/../another" },
            "query" => ManualMovieTests.Poster with { SecureUrl = ManualMovieTests.Poster.SecureUrl + "?arbitrary=true" },
            _ => ManualMovieTests.Poster with { SecureUrl = ManualMovieTests.Poster.SecureUrl.Replace("https://", "https://user:pass@") }
        };
        (await Storage().ValidateReferenceAsync(image, MovieImageKind.Poster, Ct)).Error.Should().Be(MovieImageErrors.InvalidReference);
        _sdk.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("url")]
    [InlineData("public-id")]
    [InlineData("type")]
    [InlineData("format")]
    [InlineData("dimensions")]
    public async Task Missing_or_mismatched_asset_is_never_accepted(string failure)
    {
        GetResourceResult resource = Resource(ManualMovieTests.Poster);
        switch (failure)
        {
            case "missing": resource.StatusCode = HttpStatusCode.NotFound; break;
            case "url": resource.SecureUrl += "?modified"; break;
            case "public-id": resource.PublicId = "other"; break;
            case "type": resource.Type = "private"; break;
            case "format": resource.Format = "svg"; break;
            case "dimensions": resource.Width = 0; break;
        }
        _sdk.GetResourceAsync(Arg.Any<GetResourceParamsBase>(), Arg.Any<CancellationToken?>()).Returns(resource);
        (await Storage().ValidateReferenceAsync(ManualMovieTests.Poster, MovieImageKind.Poster, Ct)).Error
            .Should().Be(MovieImageErrors.InvalidReference);
    }

    [Fact]
    public async Task Provider_outage_is_controlled_without_exposing_provider_exception()
    {
        _sdk.GetResourceAsync(Arg.Any<GetResourceParamsBase>(), Arg.Any<CancellationToken?>())
            .Returns(Task.FromException<GetResourceResult>(new HttpRequestException("secret signature")));
        var result = await Storage().ValidateReferenceAsync(ManualMovieTests.Poster, MovieImageKind.Poster, Ct);
        result.Error.Should().Be(MovieImageErrors.ProviderFailure);
        result.Error.Description.Should().NotContain("secret");
    }
}
