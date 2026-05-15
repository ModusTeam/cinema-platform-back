# AGENTS.md

This file is the **Source of Truth** for AI coding agents working on the Cinema Platform Backend. It provides deep technical context, architectural constraints, and strict coding standards to ensure consistency across the codebase.

---

## 🎬 Project Mission
**Cinema Platform Backend** is a high-concurrency, distributed system built to handle real-time movie ticket booking. It leverages **Clean Architecture** to ensure maintainability and **CQRS** for scalable read/write operations.

---

## 🛠 Tech Stack Deep Dive

- **Core**: .NET 8 / ASP.NET Core
- **Architecture**: Clean Architecture + CQRS (MediatR)
- **Database**: PostgreSQL 16 + **pgvector** (Semantic AI search)
- **Persistence**: Entity Framework Core 8 (Snake Case naming, DateTime UTC conversions)
- **Caching & Locking**: Redis (StackExchange.Redis) - used for:
    - Distributed caching
    - **Atomic Seat Locking** (Lua scripts)
    - SignalR Backplane
- **Messaging**: MassTransit + RabbitMQ (Asynchronous event-driven communication)
- **Background Jobs**: Hangfire (scheduled cleanup, AI embedding generation)
- **External Integration**: 
    - **Refit**: TMDB API, Gemini AI API
    - **gRPC**: External Loyalty Service
- **Real-time**: SignalR (Ticket/Seat status updates)
- **Security**: Identity Core + JWT Bearer + Role-based Authorization

---

## 🏗 Architectural Layers

### 1. Cinema.Domain (The Core)
*No dependencies on other projects.*
- **Entities**: Encapsulated state (private setters), factory methods, and domain logic. Inherit from `BaseEntity`.
- **Value Objects / Shared**: `EntityId<T>` (Strongly-typed IDs), `Result<T>` (Functional errors).
- **Events**: `IDomainEvent` for side-effects.
- **Exceptions**: `DomainException` for exceptional cases (rarely used, prefer `Result`).
- **Interfaces**: Definitions for infrastructure services (e.g., `IMovieInfoProvider`).

### 2. Cinema.Application (The Orchestrator)
*Depends on Domain.*
- **Features**: Folder-per-feature (e.g., `Movies/`, `Sessions/`).
    - `Commands/`: State-changing requests.
    - `Queries/`: Data-retrieval requests.
    - `Dtos/`: Data Transfer Objects (Mapped via **Mapster**).
- **Behaviours**: MediatR Pipeline Behaviors:
    - `ValidationBehavior`: Automatic FluentValidation execution.
    - `IdempotencyBehavior`: Prevents duplicate processing of `IIdempotentCommand`.
- **Common**: `IApplicationDbContext`, `ICurrentUserService`, `IEmailService`.

### 3. Cinema.Infrastructure (The Implementation)
*Depends on Application and Domain.*
- **Persistence**: `ApplicationDbContext` implementation, EF Configurations.
- **Services**: Concrete implementations of external services (Redis, Gemini, Refit, QuestPdf).
- **Messaging**: MassTransit Consumers.
- **Migrations**: Database schema evolution.

### 4. Cinema.Api (The Entry Point)
*Depends on Infrastructure and Application.*
- **Controllers**: Inherit from `ApiController`. No business logic here.
- **Middleware**: Exception handling, logging context, rate limiting.
- **Hubs**: SignalR hubs for real-time connectivity.

---

## 📏 Code Style & Strict Rules

### 1. The Result Pattern (MANDATORY)
**Never throw exceptions for business logic.** Return `Result` or `Result<T>`.
```csharp
// In Handler
if (session is null)
    return Result.Failure<Guid>(DomainErrors.Session.NotFound);

session.Cancel(); // May throw DomainException if logic is violated
await context.SaveChangesAsync(ct);
return Result.Success(session.Id);
```

### 2. Entity Encapsulation
**Entities must never have public setters.**
```csharp
public class Movie : BaseEntity
{
    public EntityId<Movie> Id { get; private set; }
    public string Title { get; private set; }

    private Movie() { } // EF Core only

    public static Movie Create(string title) => new() { Id = EntityId<Movie>.New(), Title = title };

    public void Rename(string newTitle)
    {
        if (string.IsNullOrWhiteSpace(newTitle)) throw new DomainException("Empty title");
        Title = newTitle;
    }
}
```

### 3. CQRS & MediatR
- Use **Primary Constructors** for DI in handlers.
- Handlers should be thin; move business logic into Domain entities/services.
- Always use `CancellationToken`.

### 4. Database Access
- Use `IApplicationDbContext` in Application layer.
- Use `await context.SaveChangesAsync(ct)` for all writes.
- Use `AsNoTracking()` for read-only queries in Queries.

---

## 🔄 Common Workflows

### workflow: Adding a New Feature
1. **Domain**: Add Entity and Domain Errors.
2. **Persistence**: Add `DbSet`, Configuration, and Migration.
3. **Application**: 
    - Create DTOs.
    - Create Commands/Queries.
    - Add FluentValidation.
4. **Api**: Add Controller endpoint.
5. **Infrastructure**: (Optional) Add background jobs or messaging consumers.

### workflow: Seat Locking Process
1. Client requests `LockSeatCommand`.
2. `RedisSeatLockingService` executes Lua script to set a key with TTL (10 min).
3. If successful, SignalR `TicketHub` broadcasts to other clients.
4. If payment fails/expires, `UnlockSeatCommand` is triggered.

---

## 🚫 The "NEVER" List (DO NOT DO)
- **DON'T** use `public set` on domain entities.
- **DON'T** inject `IMediator` in Controllers (use base `Mediator` property).
- **DON'T** reference `Infrastructure` or `Api` from `Application` or `Domain`.
- **DON'T** use `var` for simple types (int, string) or when type is ambiguous.
- **DON'T** skip validation rules for new commands.
- **DON'T** use `DateTime.Now` (always use `DateTime.UtcNow`).

---

## 🧪 Testing Standards
- **Unit Tests**: Test every Command and Query handler.
- **Mocks**: Use `NSubstitute` for mocking interfaces.
- **Assertions**: Use `FluentAssertions`.
- **Structure**: Mirror the Application project structure in the Tests project.

---

## 📡 Terminal Commands

```bash
# General
dotnet run --project Cinema.Api
dotnet watch --project Cinema.Api

# Database
dotnet ef migrations add <Name> --project Cinema.Infrastructure --startup-project Cinema.Api
dotnet ef database update --project Cinema.Infrastructure --startup-project Cinema.Api

# Infrastructure
docker-compose up -d # Spins up Postgres, Redis, RabbitMQ
```

---

**Last Updated**: 2026-05-14
**Maintainer**: Cinema Platform Development Team
