using System.Text.Json;
using Cinema.Api.DTOs.Movies;
using Cinema.Api.Mappings;
using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Models.MovieImages;
using Cinema.Catalog.Application.Movies.Commands.CreateMovie;
using Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Commands;
using Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Handlers;
using Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Validators;
using Cinema.Catalog.Application.Movies.Images;
using Cinema.Catalog.Application.Movies.Models;
using Cinema.Catalog.Application.Movies.Queries.GetMovieById;
using Cinema.Catalog.Application.Movies.Queries.GetMoviesWithPagination;
using Cinema.Catalog.Application.Movies.Services;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Shared;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Cinema.CatalogTests;

public class ManualMovieTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static CreateMovieCommand Basic => new("Stage 2 test", "A manually entered film.", 90, 1994, MovieStatus.Active);
    internal static MovieImageReference Poster => new(
        "https://res.cloudinary.com/test/image/upload/v1/cinema-platform/movies/posters/0123456789abcdef0123456789abcdef.png",
        "cinema-platform/movies/posters/0123456789abcdef0123456789abcdef");
    internal static MovieImageReference Backdrop => new(
        Poster.SecureUrl.Replace("/posters/", "/backdrops/"), Poster.PublicId.Replace("/posters/", "/backdrops/"));

    private static IMovieImageStorage Storage()
    {
        IMovieImageStorage storage = Substitute.For<IMovieImageStorage>();
        storage.ValidateReferenceAsync(Arg.Any<MovieImageReference>(), Arg.Any<MovieImageKind>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        return storage;
    }

    internal static CreateMovieCommandHandler Handler(CatalogTestDatabase db, IMovieImageStorage? storage = null, IBackgroundJobClient? jobs = null) =>
        new(db.Context, jobs ?? Substitute.For<IBackgroundJobClient>(), new(db.Context, storage ?? Storage()),
            NullLogger<CreateMovieCommandHandler>.Instance);

    [Fact]
    public async Task Basic_creation_defaults_external_id_and_rating_and_is_retrievable_and_listed()
    {
        await using CatalogTestDatabase db = await CatalogTestDatabase.CreateAsync();
        IBackgroundJobClient jobs = Substitute.For<IBackgroundJobClient>();
        var result = await Handler(db, jobs: jobs).Handle(Basic, Ct);
        result.IsSuccess.Should().BeTrue();
        db.Db.ChangeTracker.Clear();
        Movie movie = (await new GetMovieByIdQueryHandler(db.Context).Handle(new(result.Value), Ct)).Value;
        movie.ExternalId.Should().BeNull();
        movie.Rating.Should().Be(0);
        movie.PosterPublicId.Should().BeNull();
        var list = await new GetMoviesWithPaginationQueryHandler(db.Context).Handle(new(SearchTerm: Basic.Title), Ct);
        list.Value.Items.Should().ContainSingle(m => m.Id.Value == result.Value);
        jobs.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [Fact]
    public async Task Images_genres_and_manual_cast_round_trip_and_job_failure_does_not_fail_committed_creation()
    {
        await using CatalogTestDatabase db = await CatalogTestDatabase.CreateAsync();
        Genre first = Genre.Create(null, "Drama");
        Genre second = Genre.Create(null, "Comedy");
        db.Db.AddRange(first, second);
        await db.Db.SaveChangesAsync(Ct);
        IBackgroundJobClient jobs = Substitute.For<IBackgroundJobClient>();
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(_ => throw new InvalidOperationException("offline"));
        var command = Basic with { GenreIds = [first.Id.Value, second.Id.Value], Poster = Poster, Backdrop = Backdrop,
            Rating = 8.25m, AgeRestriction = "12+", TrailerUrl = "https://www.youtube.com/watch?v=example",
            Cast = [new ManualCastMember("Manual actor", "Lead")] };
        var result = await Handler(db, jobs: jobs).Handle(command, Ct);
        result.IsSuccess.Should().BeTrue();
        db.Db.ChangeTracker.Clear();
        Movie movie = (await new GetMovieByIdQueryHandler(db.Context).Handle(new(result.Value), Ct)).Value;
        movie.PosterUrl.Should().Be(Poster.SecureUrl);
        movie.PosterPublicId.Should().Be(Poster.PublicId);
        movie.BackdropPublicId.Should().Be(Backdrop.PublicId);
        movie.MovieGenres.Should().HaveCount(2);
        movie.Cast.Should().ContainSingle().Which.ExternalId.Should().BeNull();
        movie.AgeRestriction.Should().Be("12+");
        movie.Rating.Should().Be(8.25m);
        TypeAdapterConfig config = new();
        new MovieMappingConfig().Register(config);
        MovieDto dto = movie.Adapt<MovieDto>(config);
        dto.PosterPublicId.Should().Be(Poster.PublicId);
        dto.GenreIds.Should().BeEquivalentTo(new[] { first.Id.Value, second.Id.Value });
        dto.Cast.Should().ContainSingle().Which.Name.Should().Be("Manual actor");
    }

    [Fact]
    public async Task Unknown_genre_and_untrusted_image_fail_before_any_movie_is_saved()
    {
        await using CatalogTestDatabase db = await CatalogTestDatabase.CreateAsync();
        var missing = await Handler(db).Handle(Basic with { GenreIds = [Guid.NewGuid()] }, Ct);
        missing.Error.Code.Should().Be("Genre.NotFound");
        IMovieImageStorage storage = Storage();
        storage.ValidateReferenceAsync(Poster, MovieImageKind.Poster, Ct).Returns(Result.Failure(MovieImageErrors.InvalidReference));
        var image = await Handler(db, storage).Handle(Basic with { Poster = Poster }, Ct);
        image.Error.Should().Be(MovieImageErrors.InvalidReference);
        (await db.Db.Set<Movie>().CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Database_failure_rolls_back_movie_and_genre_associations_and_does_not_enqueue()
    {
        await using CatalogTestDatabase db = await CatalogTestDatabase.CreateAsync();
        Genre genre = Genre.Create(null, "Drama");
        db.Db.Add(genre);
        await db.Db.SaveChangesAsync(Ct);
        await db.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_movie_genre() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected genre write failure'; END $$;
            CREATE TRIGGER reject_movie_genre BEFORE INSERT ON movie_genres
            FOR EACH ROW EXECUTE FUNCTION reject_movie_genre();
            """, Ct);
        IBackgroundJobClient jobs = Substitute.For<IBackgroundJobClient>();
        Func<Task> create = () => Handler(db, jobs: jobs).Handle(Basic with { GenreIds = [genre.Id.Value] }, Ct);
        await create.Should().ThrowAsync<DbUpdateException>();
        db.Db.ChangeTracker.Clear();
        (await db.Db.Set<Movie>().CountAsync(Ct)).Should().Be(0);
        (await db.Db.Set<MovieGenre>().CountAsync(Ct)).Should().Be(0);
        jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [Fact]
    public async Task Partial_image_edits_preserve_other_fields_and_explicit_null_clears_only_the_requested_image()
    {
        await using CatalogTestDatabase db = await CatalogTestDatabase.CreateAsync();
        Guid id = (await Handler(db).Handle(Basic with { Poster = Poster, Backdrop = Backdrop,
            TrailerUrl = "https://www.youtube.com/watch?v=original" }, Ct)).Value;
        var handler = new UpdateMovieImagesCommandHandler(db.Context, new(db.Context, Storage()));
        await handler.Handle(new(id, null, null, "https://www.youtube.com/watch?v=new"), Ct);
        Movie movie = await db.Db.Set<Movie>().SingleAsync(Ct);
        movie.PosterPublicId.Should().Be(Poster.PublicId);
        movie.BackdropPublicId.Should().Be(Backdrop.PublicId);
        MovieImageReference replacement = Poster with { PublicId = Poster.PublicId.Replace("0123456789abcdef0123456789abcdef", "abcdef0123456789abcdef0123456789"),
            SecureUrl = Poster.SecureUrl.Replace("0123456789abcdef0123456789abcdef", "abcdef0123456789abcdef0123456789") };
        await handler.Handle(new(id, null, null, null, Poster: replacement), Ct);
        movie.PosterPublicId.Should().Be(replacement.PublicId);
        movie.BackdropPublicId.Should().Be(Backdrop.PublicId);
        await handler.Handle(new(id, null, null, null, SetPosterReference: true), Ct);
        movie.PosterPublicId.Should().BeNull();
        movie.PosterUrl.Should().BeNull();
        movie.BackdropUrl.Should().Be(Backdrop.SecureUrl);
    }

    [Fact]
    public async Task Failed_image_replacement_keeps_persisted_original_and_never_deletes_assets()
    {
        await using CatalogTestDatabase db = await CatalogTestDatabase.CreateAsync();
        Guid id = (await Handler(db).Handle(Basic with { Poster = Poster, Backdrop = Backdrop }, Ct)).Value;
        await db.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_movie_update() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected update failure'; END $$;
            CREATE TRIGGER reject_movie_update BEFORE UPDATE ON movies
            FOR EACH ROW EXECUTE FUNCTION reject_movie_update();
            """, Ct);
        var handler = new UpdateMovieImagesCommandHandler(db.Context, new(db.Context, Storage()));
        Func<Task> replace = () => handler.Handle(new(id, null, null, null, SetPosterReference: true), Ct);
        await replace.Should().ThrowAsync<DbUpdateException>();
        db.Db.ChangeTracker.Clear();
        (await db.Db.Set<Movie>().SingleAsync(Ct)).PosterPublicId.Should().Be(Poster.PublicId);
    }

    [Fact]
    public async Task Details_can_replace_or_clear_genres_cast_and_age_without_clearing_images()
    {
        await using CatalogTestDatabase db = await CatalogTestDatabase.CreateAsync();
        Genre genre = Genre.Create(null, "Drama");
        db.Db.Add(genre);
        await db.Db.SaveChangesAsync(Ct);
        Guid id = (await Handler(db).Handle(Basic with { Poster = Poster, AgeRestriction = "12+" }, Ct)).Value;
        var handler = new UpdateMovieDetailsCommandHandler(db.Context, new(db.Context, Storage()));
        await handler.Handle(new(id, null, null, null, null, [genre.Id.Value], "16+", true,
            [new ManualCastMember("Actor", "Lead")]), Ct);
        db.Db.ChangeTracker.Clear();
        Movie movie = (await new GetMovieByIdQueryHandler(db.Context).Handle(new(id), Ct)).Value;
        movie.MovieGenres.Should().ContainSingle();
        movie.Cast.Should().ContainSingle();
        movie.AgeRestriction.Should().Be("16+");
        movie.PosterPublicId.Should().Be(Poster.PublicId);
        await handler.Handle(new(id, null, null, null, null, [genre.Id.Value]), Ct);
        await handler.Handle(new(id, null, null, null, null, [], null, true, []), Ct);
        db.Db.ChangeTracker.Clear();
        movie = (await new GetMovieByIdQueryHandler(db.Context).Handle(new(id), Ct)).Value;
        movie.MovieGenres.Should().BeEmpty();
        movie.Cast.Should().BeEmpty();
        movie.AgeRestriction.Should().BeNull();
    }

    [Fact]
    public void Validation_rejects_malformed_duplicates_and_metadata_but_accepts_historical_and_upcoming_movies()
    {
        CreateMovieCommandValidator validator = new();
        Guid genre = Guid.NewGuid();
        CreateMovieCommand[] invalid =
        [
            Basic with { Title = new string('x', 201) }, Basic with { Description = "" },
            Basic with { DurationMinutes = 0 }, Basic with { DurationMinutes = 601 },
            Basic with { ReleaseYear = 1887 }, Basic with { ReleaseYear = DateTime.UtcNow.Year + 11 },
            Basic with { Status = (MovieStatus)0 }, Basic with { GenreIds = [genre, genre] },
            Basic with { GenreIds = [Guid.Empty] }, Basic with { Rating = -1 }, Basic with { Rating = 10.01m },
            Basic with { Rating = 7.123m }, Basic with { AgeRestriction = "21+" },
            Basic with { TrailerUrl = "javascript:alert(1)" }, Basic with { TrailerUrl = "http://example.com" },
            Basic with { Poster = new("https://example.com", "") },
            Basic with { Cast = [new("", "Lead")] }, Basic with { Cast = [new("Actor"), new(" actor ")] },
            Basic with { Cast = [null!] }
        ];
        foreach (CreateMovieCommand command in invalid) validator.Validate(command).IsValid.Should().BeFalse();
        validator.Validate(Basic).IsValid.Should().BeTrue();
        validator.Validate(Basic with { ReleaseYear = DateTime.UtcNow.Year + 3, GenreIds = [] }).IsValid.Should().BeTrue();
        new UpdateMovieDetailsValidator().Validate(new UpdateMovieDetailsCommand(Guid.NewGuid(), null, 15, 4.125, 1994)).IsValid.Should().BeFalse();
        new UpdateMovieDetailsValidator().Validate(new UpdateMovieDetailsCommand(Guid.NewGuid(), null, 15, 4.12, 1994)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Json_patch_distinguishes_omission_and_null_and_tmdb_import_retains_external_ids_and_urls()
    {
        JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
        var omitted = JsonSerializer.Deserialize<UpdateMovieImagesDto>("{}", json)!;
        var cleared = JsonSerializer.Deserialize<UpdateMovieImagesDto>("""{"poster":null}""", json)!;
        omitted.HasPoster.Should().BeFalse();
        cleared.HasPoster.Should().BeTrue();
        cleared.HasBackdrop.Should().BeFalse();
        JsonSerializer.Deserialize<UpdateMovieDetailsDto>("""{"ageRestriction":null}""", json)!.HasAgeRestriction.Should().BeTrue();
        Movie imported = Movie.Import(42, "Imported", "Overview", 100, 8, new DateTime(2000, 1, 1),
            "https://image.tmdb.org/t/p/original/poster.jpg", "https://image.tmdb.org/t/p/original/backdrop.jpg", null);
        imported.SetCast([new() { ExternalId = 123, Name = "Imported actor" }]);
        imported.ExternalId.Should().Be(42);
        imported.PosterPublicId.Should().BeNull();
        imported.SetPoster(Poster.SecureUrl, Poster.PublicId);
        imported.UpdateFromTmdb("Refreshed", "Overview", 110, 7, null, "https://image.tmdb.org/t/p/original/new.jpg", null, null);
        imported.PosterPublicId.Should().BeNull();
        imported.PosterUrl.Should().Be("https://image.tmdb.org/t/p/original/new.jpg");
        JsonSerializer.Deserialize<List<MovieCastMember>>(JsonSerializer.Serialize(imported.Cast))![0].ExternalId.Should().Be(123);
    }
}
