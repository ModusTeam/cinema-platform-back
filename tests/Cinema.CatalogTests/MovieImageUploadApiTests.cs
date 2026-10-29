using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Cinema.Api.Controllers;
using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Models.MovieImages;
using Cinema.Catalog.Application.Movies.Images;
using Cinema.Catalog.Application.Movies.Services;
using Cinema.Catalog.Infrastructure.Options;
using Cinema.Catalog.Infrastructure.Services;
using Cinema.Domain.Shared;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Xunit;

namespace Cinema.CatalogTests;

public class MovieImageUploadApiTests
{
    private const string Path = "/api/movies/images/upload";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes(
        "catalog-upload-tests-only-0123456789012345678901234567890123456789"));

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    public async Task Endpoint_enforces_jwt_and_admin_role_before_processing_files(string? role, HttpStatusCode expected)
    {
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        await using WebApplication app = await StartAsync(storage);
        using HttpClient client = app.GetTestClient();
        using HttpRequestMessage request = Request(role, Encoding.UTF8.GetBytes("<html>invalid</html>"));
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(expected);
        storage.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("poster", MovieImageKind.Poster)]
    [InlineData("backdrop", MovieImageKind.Backdrop)]
    public async Task Admin_upload_binds_multipart_and_returns_only_stable_metadata(string kind, MovieImageKind expected)
    {
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        storage.UploadAsync(Arg.Any<MovieImageUpload>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            MovieImageUpload image = call.Arg<MovieImageUpload>();
            image.Kind.Should().Be(expected);
            image.Format.Should().Be("png");
            image.Content.Position.Should().Be(0);
            return Result.Success(MovieImageUploadTests.Metadata);
        });
        await using WebApplication app = await StartAsync(storage);
        using HttpClient client = app.GetTestClient();
        using HttpRequestMessage request = Request("Admin", MovieImageUploadTests.Png, kind);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<MovieImageUploadResult>(TestContext.Current.CancellationToken))
            .Should().Be(MovieImageUploadTests.Metadata);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "secureUrl", "publicId", "assetId", "width", "height", "format", "bytes");
        await storage.Received(1).UploadAsync(Arg.Any<MovieImageUpload>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("empty-file")]
    [InlineData("missing-kind")]
    [InlineData("invalid-kind")]
    [InlineData("fake-image")]
    public async Task Invalid_requests_are_rejected_without_calling_storage(string scenario)
    {
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        await using WebApplication app = await StartAsync(storage);
        using HttpClient client = app.GetTestClient();
        byte[]? bytes = scenario switch
        {
            "missing-file" => null,
            "empty-file" => [],
            "fake-image" => Encoding.UTF8.GetBytes("<svg>disguised as image/jpeg and poster.jpg</svg>"),
            _ => MovieImageUploadTests.Png
        };
        string? kind = scenario switch { "missing-kind" => null, "invalid-kind" => "../other", _ => "poster" };
        using HttpRequestMessage request = Request("Admin", bytes, kind);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        storage.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Image_over_ten_megabytes_returns_413()
    {
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        await using WebApplication app = await StartAsync(storage);
        using HttpClient client = app.GetTestClient();
        using HttpRequestMessage request = Request("Admin", new byte[MovieImageUploadService.MaxFileBytes + 1]);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        storage.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("provider", HttpStatusCode.BadGateway)]
    [InlineData("timeout", HttpStatusCode.GatewayTimeout)]
    public async Task Provider_errors_have_controlled_problem_responses(string failure, HttpStatusCode expected)
    {
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        Error error = failure == "timeout" ? MovieImageErrors.Timeout : MovieImageErrors.ProviderFailure;
        storage.UploadAsync(Arg.Any<MovieImageUpload>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<MovieImageUploadResult>(error));
        await using WebApplication app = await StartAsync(storage);
        using HttpClient client = app.GetTestClient();
        using HttpRequestMessage request = Request("Admin", MovieImageUploadTests.Png);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(expected);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(error.Code).And.Contain("traceId");
    }

    [Fact]
    public async Task Unconfigured_real_storage_returns_503_without_startup_failure()
    {
        await using WebApplication app = await StartAsync(null);
        using HttpClient client = app.GetTestClient();
        using HttpRequestMessage request = Request("Admin", MovieImageUploadTests.Png);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("MovieImage.Unavailable");
    }

    [Fact]
    public async Task Swagger_describes_file_kind_multipart_and_success_metadata()
    {
        await using WebApplication app = await StartAsync(Substitute.For<IMovieImageStorage>());
        using HttpClient client = app.GetTestClient();
        using JsonDocument json = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json", TestContext.Current.CancellationToken));
        JsonElement operation = json.RootElement.GetProperty("paths").EnumerateObject()
            .Single(path => string.Equals(path.Name, Path, StringComparison.OrdinalIgnoreCase)).Value.GetProperty("post");
        JsonElement schema = operation.GetProperty("requestBody").GetProperty("content")
            .GetProperty("multipart/form-data").GetProperty("schema");
        schema.GetProperty("properties").GetProperty("file").GetProperty("format").GetString().Should().Be("binary");
        schema.GetProperty("properties").GetProperty("kind").GetProperty("pattern").GetString().Should().Be("^(poster|backdrop)$");
        operation.GetProperty("responses").TryGetProperty("200", out _).Should().BeTrue();
    }

    private static HttpRequestMessage Request(string? role, byte[]? bytes, string? kind = "poster")
    {
        MultipartFormDataContent form = new();
        if (bytes is not null)
        {
            ByteArrayContent file = new(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "file", "poster.jpg");
        }
        if (kind is not null) form.Add(new StringContent(kind), "kind");
        HttpRequestMessage request = new(HttpMethod.Post, Path) { Content = form };
        if (role is not null)
        {
            JwtSecurityToken token = new("catalog-tests", "catalog-tests",
                [new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)],
                expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new(SigningKey, SecurityAlgorithms.HmacSha256));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        }
        return request;
    }

    private static async Task<WebApplication> StartAsync(IMovieImageStorage? storage)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(MoviesController).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options => options.CustomSchemaIds(type => type.ToString().Replace("+", ".")));
        builder.Services.AddSingleton(Substitute.For<IBackgroundJobClient>());
        builder.Services.AddScoped<MovieImageUploadService>();
        if (storage is not null) builder.Services.AddSingleton(storage);
        else
        {
            builder.Services.AddOptions<CloudinaryOptions>().ValidateDataAnnotations();
            builder.Services.AddScoped<IMovieImageStorage, CloudinaryMovieImageStorage>();
        }
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = "catalog-tests",
                ValidAudience = "catalog-tests",
                IssuerSigningKey = SigningKey,
                RoleClaimType = ClaimTypes.Role,
                NameClaimType = JwtRegisteredClaimNames.Sub
            };
        });
        builder.Services.AddAuthorization();
        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseSwagger();
        app.MapControllers();
        try
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }
}
