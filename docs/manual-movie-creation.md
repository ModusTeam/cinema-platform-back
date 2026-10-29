# Manual movie creation (Stage 2)

Backend-only integration contract. Use the existing Admin JWT and existing
`POST /api/movies`; manual creation never calls TMDB. No frontend changes.

## Request and validation

Required properties:

| Property | Rule |
| --- | --- |
| `title` | Nonblank, at most 200 characters |
| `description` | Nonblank, at most 2,000 characters |
| `durationMinutes` | Integer, 1–600 minutes (short films supported) |
| `releaseYear` | Integer, 1888 through the current UTC year + 10 |
| `status` | `1` ComingSoon, `2` Active, `3` Archived |

Optional properties:

| Property | Rule / omission behavior |
| --- | --- |
| `genreIds` | At most 50 distinct, nonempty GUIDs for existing genres; omitted/null/[] means no genres on creation |
| `rating` | Decimal 0–10, at most two decimal places; omitted/null defaults to 0 |
| `ageRestriction` | null, `0+`, `7+`, `12+`, `16+`, `18+` |
| `poster`, `backdrop` | null or `{ "secureUrl": "...", "publicId": "..." }` from the upload endpoint |
| `trailerUrl` | null or absolute HTTPS URL without credentials, at most 500 characters |
| `cast` | Up to 50 distinct name/role pairs; name required, name/role up to 200 characters |

Manual cast entries accept only `name` and optional `role`. They do not need
TMDB IDs or photos. Names/roles are trimmed for persistence. Their stored JSON
`ExternalId` is null; existing imported numeric IDs retain their JSON format.

There is one authoritative creation validator, registered by the existing
assembly scan and executed by the existing MediatR validation behavior. The
duplicate details validators were also consolidated so a short/historical
movie can subsequently be edited. Genre existence and Cloudinary checks run
before adding the movie. Unknown genres return 404 `Genre.NotFound`;
malformed/duplicate genre IDs return 400.

Creation returns HTTP 200 with a JSON GUID string, following existing conventions.
Unauthenticated requests return 401; authenticated non-Admins return 403.
Validation returns ProblemDetails with `code` and `detail`; the existing
pipeline reports the first validation failure.

## Images and persistence

`POST /api/movies/images/upload` remains Admin-only multipart form data,
with `kind=poster|backdrop` and `file` (JPEG/PNG/WebP, at most 10 MiB).
Use the returned `secureUrl` and `publicId` unchanged. Other upload metadata
is not needed in the create request.

Verification uses the authenticated Cloudinary Admin resource API for the
configured account, never an HTTP request to the supplied delivery URL. It checks:

- Exact application folder: `cinema-platform/movies/posters` or
  `cinema-platform/movies/backdrops`, followed by the server-generated GUID.
- HTTPS `res.cloudinary.com/<configured-cloud>/image/upload/...`, without
  credentials, custom ports, queries or fragments.
- Existing provider resource with matching public ID and exact original secure
  URL, image/upload type, positive dimensions/bytes, and supported raster format.

These folders are reserved for this application's server-side uploads.
Do not place unrelated assets there, rename uploaded assets, or overwrite them
outside the application. Asset IDs and folder names alone are not accepted
without an authenticated provider lookup. Cloudinary Admin API limits apply.
Unavailable/misconfigured storage returns 503, provider failures 502, and
timeouts 504. Invalid or missing asset references return 400
`MovieImage.Validation.Reference`. No provider credentials/signatures are returned.

URLs remain in `PosterUrl`/`BackdropUrl`; nullable `PosterPublicId` and
`BackdropPublicId` retain ownership metadata. Movie `ExternalId` always stays
null for manual creation. Movie, cast JSON and genre associations use one
`SaveChangesAsync` and EF's implicit transaction; failed association insertion
rolls back the movie too.

Migration: `20261029212722_AddManagedMovieImages` adds only the two nullable
varchar(255) movie columns. The snapshot also records nullable cast ExternalId
inside the existing JSON aggregate; no JSON rewrite or extra table is needed.
Apply through the established migration/updater workflow before using this API.

`GET /api/movies/{id}` adds `posterPublicId`, `backdropPublicId`, and
`genreIds` to the existing DTO. Existing URL, genre-name and cast presentation
fields remain. Successful manual creation and existing Admin edits/delete/restore
invalidate the tagged movie-list output cache. Caching remains enabled.
Cache failures are logged after commit; the existing 60-second TTL is the fallback.
The optional embedding job is queued once after commit. Queue failure is logged
and the created ID is still returned; no automatic retry/outbox was introduced.

TMDB import endpoint, external IDs, synchronization and scheduling remain in
place. Imported URLs have null managed public IDs. Refreshing from TMDB replaces
image URLs as before and clears any now-obsolete managed IDs without deleting assets.

## Editing compatibility

Keep the existing title/details/images/status routes.

- Details: existing description/duration/rating/year behavior is retained.
  Optional `genreIds` replaces the selection; [] clears it, omitted/null leaves
  it unchanged. Optional `cast` replaces manual entries; [] clears it,
  omitted/null leaves it unchanged. Omitted `ageRestriction` preserves it;
  explicit null clears it. Creation's numeric, age and collection rules also apply.
- Images: omitted properties preserve the existing value, explicit null clears
  only that property. `poster` and `backdrop` accept verified upload references.
  Legacy `posterUrl`/`backdropUrl` remain supported for unchanged existing
  URLs and HTTPS `image.tmdb.org/t/p/...` URLs. Other newly supplied image URLs
  require the typed upload reference. Do not supply both the reference and legacy
  URL for the same image, including explicit nulls.
- `trailerUrl` remains on the images patch and uses the HTTPS rule.
- Status still accepts the numeric enum body; undefined statuses are rejected.

Explicit null on an image patch now clears that image; a client wishing to
preserve it must omit the property. This corrects the previous behavior that
cleared unrelated images when any field was omitted.

## Retention and manual cleanup policy

Replacement, clearing, soft deletion, failed persistence and TMDB refresh never
delete Cloudinary assets. This preserves shared images, rollback safety and
soft-delete/restore behavior. There is no automatic cleanup job or generic
media-management endpoint.

Abandoned and replaced uploads are retained until an operator performs maintenance:

1. Pause **all** application instances and movie-writing/import processes sharing
   the account/database. Complete or cancel any in-flight uploads/writes.
2. Select only confirmed application uploads in the reserved folder with the
   generated GUID public-ID shape. Verify image/upload metadata, configured
   account and original secure URL through the authenticated provider API.
3. Check **all** movie rows, including soft-deleted/imported movies, for both
   public IDs and URLs. Also check any other environment/database sharing that
   Cloudinary account. A matching reference anywhere means retain the asset.
4. Delete only verified unreferenced candidates through an authenticated operator
   request to Cloudinary, then resume writers. Never delete by folder or prefix.

Reference check (parameterized; do not exclude soft-deleted movies):

```sql
SELECT id FROM movies
WHERE poster_public_id = @publicId OR backdrop_public_id = @publicId
   OR poster_url = @secureUrl OR backdrop_url = @secureUrl;
```

This intentionally trades storage retention for a race-free cleanup process.
Admins cancelling creation do not need to send another request. If maintenance
cannot establish exclusive access and zero references, retain the upload.

## Frontend integration examples

Obtain existing genre IDs with `GET /api/genres`.

Upload (same endpoint/response as Stage 1):

```bash
curl -X POST "$API/api/movies/images/upload" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -F "kind=poster" -F "file=@poster.png"
```

Save the request below as `movie.json`; replace the genre GUID with an existing
one and the image pair with the **actual unmodified upload response**. Omit
`poster` entirely if no image was uploaded. Upload `kind=backdrop` separately
to populate the optional `backdrop` reference.

```json
{
  "title": "Manual movie",
  "description": "A manually entered film.",
  "durationMinutes": 95,
  "releaseYear": 1994,
  "status": 2,
  "genreIds": ["11111111-1111-1111-1111-111111111111"],
  "ageRestriction": "12+",
  "rating": 8.25,
  "poster": {
    "secureUrl": "https://res.cloudinary.com/CLOUD/image/upload/vVERSION/cinema-platform/movies/posters/GUID.png",
    "publicId": "cinema-platform/movies/posters/GUID"
  },
  "trailerUrl": "https://www.youtube.com/watch?v=VIDEO_ID",
  "cast": [{"name": "Actor name", "role": "Character name"}]
}
```

```bash
curl -X POST "$API/api/movies" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" --data-binary @movie.json
# Response: "created-movie-guid"
curl "$API/api/movies/$MOVIE_ID"
curl "$API/api/movies?searchTerm=Manual"
```

Patch bodies, with `Authorization: Bearer <Admin JWT>` and
`Content-Type: application/json`:

```http
PATCH /api/movies/{id}/title
{"title":"New title"}

PATCH /api/movies/{id}/details
{"genreIds":[],"ageRestriction":null,"rating":7.5,"cast":[{"name":"Actor","role":"Lead"}]}

PATCH /api/movies/{id}/images
{"poster":{"secureUrl":"<exact uploaded secureUrl>","publicId":"<exact uploaded publicId>"}}

PATCH /api/movies/{id}/images
{"poster":null}

PATCH /api/movies/{id}/images
{"trailerUrl":"https://www.youtube.com/watch?v=VIDEO_ID"}

PATCH /api/movies/{id}/status
1
```

## Focused verification

Run Catalog tests against local PostgreSQL using disposable databases:

```powershell
$env:CINEMA_CATALOG_TEST_CONNECTION = 'Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres_password'
dotnet test --project tests/Cinema.CatalogTests
dotnet test --project tests/Cinema.ArchitectureTests -- --filter-class '*DependencyRules'
dotnet build src/Host/Cinema.Api/Cinema.Api.csproj
git diff --check
```

The Catalog PostgreSQL helper requires localhost and CREATEDB permission, creates
only Catalog tables in uniquely named test databases and drops only those databases.
Without that opt-in connection, PostgreSQL/API tests explicitly skip. Provider
verification tests use mocked Cloudinary SDK responses.

## Changed files

- `docs/manual-movie-creation.md`
- `src/Cinema.Infrastructure/Migrations/20261029212722_AddManagedMovieImages.Designer.cs`
- `src/Cinema.Infrastructure/Migrations/20261029212722_AddManagedMovieImages.cs`
- `src/Cinema.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs`
- `src/Host/Cinema.Api/Controllers/MoviesController.cs`
- `src/Host/Cinema.Api/DTOs/Movies/MovieDto.cs`
- `src/Host/Cinema.Api/DTOs/Movies/UpdateMovieDtos.cs`
- `src/Host/Cinema.Api/Filters/MovieCacheInvalidationFilter.cs`
- `src/Host/Cinema.Api/Mappings/MovieMappingConfig.cs`
- `src/Host/Cinema.Api/Modules/DependencyInjection.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Common/Interfaces/IMovieImageStorage.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Common/Models/MovieImages/MovieImageReference.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/ConfigureServices.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Commands/CreateMovie/CreateMovieCommand.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Commands/CreateMovie/CreateMovieCommandHandler.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Commands/CreateMovie/CreateMovieCommandValidator.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Commands/CreateMovie/CreateMovieValidator.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Commands/UpdateMovie/Commands/UpdateMovieDetailsCommand.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Commands/UpdateMovie/Commands/UpdateMovieDetailsCommandValidator.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Commands/UpdateMovie/Commands/UpdateMovieImagesCommand.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Commands/UpdateMovie/Handlers/UpdateMovieDetailsCommandHandler.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Commands/UpdateMovie/Handlers/UpdateMovieImagesCommandHandler.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Commands/UpdateMovie/Validators/UpdateMovieDetailsValidator.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Commands/UpdateMovie/Validators/UpdateMovieImagesValidator.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Commands/UpdateMovie/Validators/UpdateMovieStatusValidator.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Constants/MovieConstants.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Images/MovieImageErrors.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Models/ManualCastMember.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Services/ManualMovieReferences.cs`
- `src/Modules/Catalog/Cinema.Catalog.Application/Movies/Validation/MovieInputRules.cs`
- `src/Modules/Catalog/Cinema.Catalog.Domain/Entities/Movie.cs`
- `src/Modules/Catalog/Cinema.Catalog.Infrastructure/Persistence/Configurations/MovieConfiguration.cs`
- `src/Modules/Catalog/Cinema.Catalog.Infrastructure/Services/CloudinaryMovieImageStorage.cs`
- `tests/Cinema.CatalogTests/CatalogTestDatabase.cs`
- `tests/Cinema.CatalogTests/CloudinaryMovieReferenceTests.cs`
- `tests/Cinema.CatalogTests/ManualMovieApiTests.cs`
- `tests/Cinema.CatalogTests/ManualMovieTests.cs`

## Stage 2 verification results

- Targeted API build succeeded (0 errors).
- Catalog project: 81 passed, 0 failed, 0 skipped, including actual PostgreSQL persistence/rollback tests.
- DependencyRules architecture class: 25 passed, 0 failed, 0 skipped.
- Final git diff --check passed.
- Live HTTP: unauthenticated creation 401; non-Admin creation 403; invalid movie 400;
  missing genre 404; valid creation 200 with ID; detail and warmed-list retrieval 200
  with correct saved metadata and genre IDs. A dedicated fixture genre was created
  because the disposable clone initially had no genres.
- Real Cloudinary poster upload 200; creation with provider-verified reference 200;
  persisted URL/public ID retrieved successfully; public HTTPS image delivery 200;
  trailer-only edit preserved the image; untrusted image host rejected with 400.
- Live tests used an isolated clone. The temporary API was stopped, the clone and
  all Catalog test databases removed, and the test-only Cloudinary poster destroyed
  after authenticated ownership verification and a zero-reference check in the
  preserved original database. Temporary logs/state were removed. Nothing remains
  to clean up, and no original database migration or Docker volume was changed.
- Existing NuGet advisory warnings remain for MessagePack 2.5.301 and
  Microsoft.AspNetCore.SignalR.Common 8.0.23; dependencies were outside this scope.
- Actual Gemini embedding generation, live TMDB import/refresh and the full solution
  suite were not exercised. Shared TMDB image/cast compatibility has focused regression
  coverage; Cloudinary backdrop verification has mocked provider coverage.

Suggested commit: `feat(catalog): complete manual movie creation with Cloudinary assets`.
