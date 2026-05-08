# AGENTS.md

This file provides context and instructions for AI coding agents (Claude, GitHub Copilot, Cursor, etc.) working on the Cinema Platform Backend project.

---

## Project Context

**Cinema Platform Backend** is an ASP.NET Core Web API built using **Clean Architecture** and **CQRS**. It serves as the backend for a cinema ticket booking platform, handling movies, sessions, seats, ticketing, and user management.

---

## Tech Stack

```yaml
Framework: .NET 8 / ASP.NET Core
Architecture: Clean Architecture + CQRS
Mediator Pattern: MediatR
ORM: Entity Framework Core
Database: PostgreSQL (with pgvector for embeddings)
Caching & Locking: Redis (StackExchange.Redis)
Messaging: MassTransit + RabbitMQ
Background Jobs: Hangfire
External HTTP Clients: Refit
Validation: FluentValidation
Authentication: ASP.NET Core Identity + JWT Bearer
```

---

## File Structure

The solution `Cinema.sln` is split into four main projects following Clean Architecture principles:

```
Cinema.sln
├── Cinema.Domain/           # Core business logic and entities (No dependencies)
│   ├── Entities/            # Domain entities (Movie, Session, Ticket, etc.)
│   ├── Enums/               # Domain enumerations
│   ├── Exceptions/          # Custom domain exceptions
│   ├── Interfaces/          # Interfaces implemented by outer layers
│   └── Shared/              # Shared patterns (Result<T>, Error)
├── Cinema.Application/      # Application use cases (Depends on Domain)
│   ├── Common/              # Interfaces, Behaviours (Validation), Settings
│   ├── {Feature}/           # Feature folders (Movies, Sessions, Tickets)
│   │   ├── Commands/        # CQRS Commands and Handlers
│   │   ├── Queries/         # CQRS Queries and Handlers
│   │   └── Dtos/            # Data Transfer Objects
├── Cinema.Infrastructure/   # External concerns (Depends on Application & Domain)
│   ├── Persistence/         # EF Core DbContext, Configurations, Migrations
│   ├── Messaging/           # MassTransit consumers
│   └── Services/            # External service implementations (Refit, Hangfire)
└── Cinema.Api/              # Presentation layer (Depends on Application & Infrastructure)
    ├── Controllers/         # ASP.NET Core API Controllers
    ├── Middleware/          # Custom middleware
    └── Program.cs           # Dependency injection and app setup
```

---

## Code Style & Conventions

### Clean Architecture Rules

**Rule 1:** Dependencies always point inwards.
- `Cinema.Api` -> `Cinema.Infrastructure` -> `Cinema.Application` -> `Cinema.Domain`
- **Never** reference `Infrastructure` or `Api` from `Application` or `Domain`.

### CQRS with MediatR

All business use cases are structured as Commands (modify state) or Queries (read state).

**Command Pattern:**
```csharp
public record CreateMovieCommand(
    string Title,
    string Description,
    int DurationMinutes
) : IRequest<Result<Guid>>;

public class CreateMovieCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<CreateMovieCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateMovieCommand request, CancellationToken ct)
    {
        // Implementation
        return Result.Success(id);
    }
}
```

**Validation:**
Always use FluentValidation in the `Application` layer, named `{CommandName}Validator`.
```csharp
public class CreateMovieValidator : AbstractValidator<CreateMovieCommand>
{
    public CreateMovieValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DurationMinutes).GreaterThan(0);
    }
}
```

### API Controllers

Always inherit from `ApiController` (which provides `Mediator` and `HandleResult`). Do not inject `IMediator` manually in constructors.

```csharp
[Authorize]
public class MoviesController : ApiController
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        return HandleResult(await Mediator.Send(new GetMovieByIdQuery(id)));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMovieCommand command)
    {
        return HandleResult(await Mediator.Send(command));
    }
}
```

### Result Pattern

Do not throw exceptions for control flow. Use the `Result` and `Result<T>` types from `Cinema.Domain.Shared`.

- Success: `Result.Success(value)`
- Failure: `Result.Failure(DomainErrors.Movie.NotFound)`

### Domain Modeling

- Entities must encapsulate their state. Properties should have `private set;`.
- Use static factory methods (e.g., `CreateManual`) or constructors for instantiation.
- Modify state only via domain methods (e.g., `ChangeStatus`, `Rename`).
- Inherit from `BaseEntity`. For primary keys, strongly-typed IDs are used (e.g., `EntityId<Movie>`).

```csharp
public class Movie : BaseEntity
{
    public EntityId<Movie> Id { get; private set; }
    public string Title { get; private set; }
    
    private Movie() { } // For EF Core

    public static Movie CreateManual(string title)
    {
        return new Movie { Id = new EntityId<Movie>(Guid.NewGuid()), Title = title };
    }

    public void Rename(string newTitle)
    {
        if (string.IsNullOrWhiteSpace(newTitle)) throw new DomainException("Title cannot be empty.");
        Title = newTitle;
    }
}
```

---

## Common Patterns

### Background Jobs

Use `IBackgroundJobClient` from **Hangfire** for fire-and-forget or scheduled tasks.

```csharp
public class ImportMovieCommandHandler(IBackgroundJobClient jobClient)
{
    public Task<Result> Handle(ImportMovieCommand request, CancellationToken ct)
    {
        jobClient.Enqueue<IAiEmbeddingService>(s => s.UpdateMovieEmbeddingAsync(request.Id, CancellationToken.None));
        return Task.FromResult(Result.Success());
    }
}
```

### External Services (HTTP)

Always use **Refit** for external HTTP API calls. Define the interface in `Application` or `Domain`, and register it in `ConfigureInfrastructureServices.cs`.

```csharp
// Definition
public interface ITmdbApi
{
    [Get("/search/movie")]
    Task<TmdbSearchResponse> SearchMovieAsync([Query] string query);
}

// Registration in ConfigureInfrastructureServices.cs
services.AddRefitClient<ITmdbApi>().ConfigureHttpClient(...);
```

### Messaging

Use **MassTransit** to publish and consume events via RabbitMQ.
- Consumers live in `Cinema.Infrastructure/Messaging/Consumers`.
- Registration is in `AddMessaging` within `ConfigureInfrastructureServices.cs`.

---

## Agent Behavior

When working on this codebase:

1. **Follow Clean Architecture**: Keep the domain isolated. Place database logic in infrastructure. Use Application for orchestration.
2. **Embrace CQRS**: Create separate Command/Query records and Handlers. Don't mix read and write operations.
3. **Use the Result pattern**: Avoid throwing exceptions for validation or business logic errors. Return a `Result.Failure`.
4. **Leverage the base ApiController**: Use `HandleResult` to map `Result` objects to proper HTTP status codes.
5. **No direct DI in Controllers**: Use the `Mediator` property provided by `ApiController`.
6. **Primary Constructors**: Use C# 12 primary constructors for dependency injection where appropriate.
7. **Entity Encapsulation**: Never use `public set` on domain entities. Use domain methods.
8. **Keep Handlers Thin**: Business logic belongs in the Domain entities. Handlers should orchestrate fetching data, calling entity methods, and saving.

## Commands

```bash
# Development
dotnet run --project Cinema.Api
dotnet watch --project Cinema.Api

# Database Migrations
dotnet ef migrations add <Name> --project Cinema.Infrastructure --startup-project Cinema.Api
dotnet ef database update --project Cinema.Infrastructure --startup-project Cinema.Api
dotnet ef migrations remove --project Cinema.Infrastructure --startup-project Cinema.Api

# Testing
dotnet test
dotnet test --filter "Category=Unit"
dotnet test --collect:"XPlat Code Coverage"

# Docker
docker-compose up -d     # Start PostgreSQL + Redis + RabbitMQ
docker-compose down
```

## Workflows

### Adding a New Feature
1. Create domain entity
2. Configure EF Core entity mapping
3. Create migration and update database
4. Create DTOs
5. Create Query handler
6. Create Command handler
7. Add FluentValidation rules
8. Add endpoints to Controller
9. Register dependencies in DI
10. Write Unit tests

### Adding a Background Job
1. Define interface in Application layer
2. Implement interface in Infrastructure layer
3. Register service in Dependency Injection
4. Enqueue job using `IBackgroundJobClient` in handler

### Adding a New Domain Error
1. Open `DomainErrors.cs`
2. Add static class for the entity
3. Define `Error` constants

## Rules

### DO:
- Always return `Result<T>` from handlers — never throw for business logic
- Always validate with FluentValidation in Application layer
- Always use `private set` on domain entity properties
- Always use `ApiController` base class — never inject `IMediator` manually
- Always use C# 12 primary constructors for DI
- Place HTTP client interfaces in Application/Domain, implementations in Infrastructure
- Use `IApplicationDbContext` interface in handlers — never inject `DbContext` directly
- Write unit tests for all Command and Query handlers

### DON'T:
- Don't reference `Infrastructure` or `Api` from `Application` or `Domain`
- Don't use `public set` on domain entities
- Don't throw exceptions for validation — use `Result.Failure`
- Don't put business logic in Controllers or Handlers — it belongs in Domain entities
- Don't use raw SQL unless EF Core cannot handle the query
- Don't skip the ValidationBehaviour pipeline for commands
- Don't commit debug `Console.WriteLine` statements
- Don't use `var` when the type is not obvious from context

## Testing Conventions

- **Framework**: XUnit + FluentAssertions + NSubstitute
- **Location**: `Cinema.Tests/` project mirroring Application structure
- **Pattern**: Arrange / Act / Assert
- **Example**:
  ```csharp
  // Arrange
  var context = Substitute.For<IApplicationDbContext>();
  var handler = new CreateMovieCommandHandler(context);
  // Act
  var result = await handler.Handle(command, CancellationToken.None);
  // Assert
  result.IsSuccess.Should().BeTrue();
  ```

## Security Guidelines

- JWT validated via ASP.NET Core Identity + Bearer middleware
- Always apply `[Authorize]` by default; use `[AllowAnonymous]` explicitly
- Never expose internal exception details — use global exception middleware
- Validate all input via FluentValidation pipeline before handlers
- Use parameterized queries only via EF Core
- Namespace Redis keys: `cinema:{entity}:{id}`

## Domain-Specific Terms

- **Session**: scheduled movie screening at a specific Hall and time
- **Seat**: physical seat in a Hall with row/number identifiers
- **Ticket**: booking of a Seat for a Session by a User
- **Hall**: cinema auditorium with defined seat layout
- **Embedding**: pgvector float[] on Movie for semantic AI search
- **Result<T>**: discriminated union (Success/Failure) instead of exceptions
- **EntityId<T>**: strongly-typed Guid wrapper to prevent ID mix-ups

---

**Last Updated**: 2026-05-05
**Maintained By**: Development Team
