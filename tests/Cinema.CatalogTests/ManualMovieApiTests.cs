using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Cinema.Api.Controllers;
using Cinema.Api.Filters;
using Cinema.Application.Common.Behaviours;
using Cinema.Application.Common.Interfaces;
using Cinema.Catalog.Application;
using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Catalog.Application.Movies.Commands.CreateMovie;
using Cinema.Catalog.Domain.Entities;
using Cinema.Domain.Shared;
using FluentAssertions;
using Hangfire;
using MediatR;
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

public class ManualMovieApiTests
{
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes(
        "catalog-creation-tests-only-0123456789012345678901234567890123456789"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("User", HttpStatusCode.Forbidden)]
    public async Task Create_requires_authenticated_admin(string? role, HttpStatusCode expected)
    {
        await using CatalogTestDatabase db = await CatalogTestDatabase.CreateAsync();
        await using WebApplication app = await StartAsync(db);
        using HttpClient client = app.GetTestClient();
        Authorize(client, role);
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/movies", ManualMovieTests.Basic, Ct);
        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task Create_returns_controlled_validation_and_missing_genre_errors()
    {
        await using CatalogTestDatabase db = await CatalogTestDatabase.CreateAsync();
        await using WebApplication app = await StartAsync(db);
        using HttpClient client = app.GetTestClient();
        Authorize(client, "Admin");
        using HttpResponseMessage invalid = await client.PostAsJsonAsync("/api/movies", ManualMovieTests.Basic with { Title = "" }, Ct);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().Should().Be("Validation.Title");
        using HttpResponseMessage missing = await client.PostAsJsonAsync("/api/movies",
            ManualMovieTests.Basic with { GenreIds = [Guid.NewGuid()] }, Ct);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missing.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().Should().Be("Genre.NotFound");
    }

    [Fact]
    public async Task Creation_invalidates_cached_listing_and_images_patch_preserves_omitted_fields()
    {
        await using CatalogTestDatabase db = await CatalogTestDatabase.CreateAsync();
        Genre genre = Genre.Create(null, "Drama");
        db.Db.Add(genre);
        await db.Db.SaveChangesAsync(Ct);
        await using WebApplication app = await StartAsync(db);
        using HttpClient client = app.GetTestClient();
        const string listing = "/api/movies?searchTerm=Stage";
        var before = await client.GetFromJsonAsync<JsonElement>(listing, Ct);
        before.GetProperty("totalCount").GetInt32().Should().Be(0);
        Authorize(client, "Admin");
        using HttpResponseMessage create = await client.PostAsJsonAsync("/api/movies",
            ManualMovieTests.Basic with { Poster = ManualMovieTests.Poster, Backdrop = ManualMovieTests.Backdrop,
                GenreIds = [genre.Id.Value] }, Ct);
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        Guid id = await create.Content.ReadFromJsonAsync<Guid>(Ct);
        Authorize(client, null);
        (await client.GetFromJsonAsync<JsonElement>(listing, Ct)).GetProperty("totalCount").GetInt32().Should().Be(1);
        Authorize(client, "Admin");
        using HttpResponseMessage edit = await client.PatchAsJsonAsync($"/api/movies/{id}/images",
            new { trailerUrl = "https://www.youtube.com/watch?v=example" }, Ct);
        edit.StatusCode.Should().Be(HttpStatusCode.OK);
        var movie = await client.GetFromJsonAsync<JsonElement>($"/api/movies/{id}", Ct);
        movie.GetProperty("posterPublicId").GetString().Should().Be(ManualMovieTests.Poster.PublicId);
        movie.GetProperty("backdropPublicId").GetString().Should().Be(ManualMovieTests.Backdrop.PublicId);
        movie.GetProperty("genreIds").EnumerateArray().Single().GetGuid().Should().Be(genre.Id.Value);
        using HttpResponseMessage clear = await client.PatchAsJsonAsync($"/api/movies/{id}/images", new { poster = (object?)null }, Ct);
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        var cleared = await client.GetFromJsonAsync<JsonElement>($"/api/movies/{id}", Ct);
        cleared.GetProperty("posterPublicId").ValueKind.Should().Be(JsonValueKind.Null);
        cleared.GetProperty("backdropPublicId").GetString().Should().Be(ManualMovieTests.Backdrop.PublicId);
        Authorize(client, null);
        var after = await client.GetFromJsonAsync<JsonElement>(listing, Ct);
        after.GetProperty("totalCount").GetInt32().Should().Be(1);
        Authorize(client, "Admin");
        using HttpResponseMessage deleted = await client.DeleteAsync($"/api/movies/{id}", Ct);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Authorize(client, null);
        (await client.GetFromJsonAsync<JsonElement>(listing, Ct)).GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    private static void Authorize(HttpClient client, string? role)
    {
        if (role is null) { client.DefaultRequestHeaders.Authorization = null; return; }
        JwtSecurityToken token = new("catalog-tests", "catalog-tests",
            [new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new(SigningKey, SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    private static async Task<WebApplication> StartAsync(CatalogTestDatabase db)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(MoviesController).Assembly);
        new Cinema.Api.Mappings.MovieMappingConfig().Register(Mapster.TypeAdapterConfig.GlobalSettings);
        builder.Services.AddCatalogApplication();
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        builder.Services.AddSingleton<IApplicationDbContext>(db.Context);
        builder.Services.AddSingleton(Substitute.For<IBackgroundJobClient>());
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        storage.ValidateReferenceAsync(Arg.Any<Cinema.Catalog.Application.Common.Models.MovieImages.MovieImageReference>(),
            Arg.Any<Cinema.Catalog.Application.Common.Models.MovieImages.MovieImageKind>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        builder.Services.AddSingleton(storage);
        builder.Services.AddOutputCache();
        builder.Services.AddScoped<MovieCacheInvalidationFilter>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
                ValidIssuer = "catalog-tests", ValidAudience = "catalog-tests", IssuerSigningKey = SigningKey, RoleClaimType = ClaimTypes.Role
            });
        builder.Services.AddAuthorization();
        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseOutputCache();
        app.MapControllers();
        try { await app.StartAsync(Ct); return app; }
        catch { await app.DisposeAsync(); throw; }
    }
}
