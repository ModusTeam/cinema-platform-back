# 🎬 Cinema Platform API

Backend API for a Cinema Management System developed as part of the **SoftServe Practice**. This solution provides a comprehensive RESTful API for managing movies, sessions, halls, and ticket bookings using Clean Architecture principles.

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat&logo=dotnet)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-336791?style=flat&logo=postgresql)
![Redis](https://img.shields.io/badge/Redis-Cache-DC382D?style=flat&logo=redis)
![Docker](https://img.shields.io/badge/Docker-Compose-2496ED?style=flat&logo=docker)
![License](https://img.shields.io/badge/License-MIT-green)

---

## 🏗️ Architecture & Tech Stack

The project follows **Clean Architecture** combined with the **CQRS** (Command Query Responsibility Segregation) pattern.

| Layer | Technologies |
|---|---|
| **API** | ASP.NET Core 8 Web API, Swagger / OpenAPI |
| **Application** | MediatR (CQRS), FluentValidation, DTOs |
| **Domain** | Entities, Enums, Domain Exceptions, Result pattern |
| **Infrastructure** | EF Core, PostgreSQL, Redis, Refit (TMDB), Hangfire, Serilog |
| **Auth** | ASP.NET Core Identity, JWT (Access + Refresh Tokens) |
| **DevOps** | Docker & Docker Compose |

---

## 📂 Project Structure

```
Cinema.Api/              # Controllers, Middleware, Entry Point
├── Controllers/         # Account, Auth, Halls, Movies, Seats, Sessions, etc.
├── ExceptionHandlers/   # GlobalExceptionHandler
├── Middleware/          # RequestLogContextMiddleware
└── Services/           # CurrentUserService

Cinema.Application/      # Business Logic, CQRS Handlers, DTOs, Validators
├── Account/            # Profile & password commands/queries
├── Auth/               # Login, Register, RefreshToken
├── Halls/              # Hall CRUD + pagination
├── Movies/             # Movie CRUD + TMDB import
├── Seats/              # Seat types, locking logic
├── Sessions/           # Session scheduling, reschedule, cancel
├── Technologies/       # Hall technologies (IMAX, 3D, etc.)
└── Users/              # Role management

Cinema.Domain/           # Core: Entities, Enums, Exceptions, Interfaces
├── Entities/           # Movie, Hall, Session, Seat, Order, Ticket, etc.
├── Enums/              # MovieStatus, SeatStatus, SessionStatus, etc.
└── Shared/             # Result<T>, Error

Cinema.Infrastructure/   # Data, Caching, External APIs, Identity
├── Persistence/        # EF Core DbContext, Configurations, Migrations
└── Services/           # Identity, Token, Redis SeatLocking, TMDB, UserService
```

---

## 🚀 Getting Started

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop)
- [PostgreSQL](https://www.postgresql.org/) & [Redis](https://redis.io/) *(if running locally without Docker)*

### Option 1 — Docker Compose *(Recommended)*

Spins up the API, PostgreSQL, Redis, and Redis Commander in one command:

```bash
docker-compose up -d --build
```

The API will be available at `http://localhost:5000`.

### Option 2 — Run Locally

1. Clone the repository:
```bash
git clone https://github.com/your-username/cinema-platform-backend.git
cd cinema-platform-backend
```

2. Update `ConnectionStrings` in `Cinema.Api/appsettings.json` to point to your local PostgreSQL & Redis.

3. Restore, migrate, and run:
```bash
dotnet restore
dotnet ef database update --project Cinema.Infrastructure --startup-project Cinema.Api
dotnet run --project Cinema.Api
```

---

## 🗺️ Roadmap & Requirements

Progress tracking based on **SoftServe Practice** requirements.

### 👤 Administrator

- [x] **Movie Management** — Create, update, delete movies (including TMDB import)
- [x] **Session Management** — Schedule sessions, detect overlaps, manage pricing
- [x] **Hall Management** — Configure halls, seat layouts, and technologies (IMAX, 3D, etc.)
- [ ] **Sales Statistics** — View sales stats and key metrics *(Extra Task)*

### 👤 Client

- [x] **Browse Offers** — View current movies and new releases
- [x] **Schedule Filtering** — View sessions with filters by date, time, and genre
- [x] **Movie Details** — Description, trailers, cast, ratings
- [x] **Authentication** — Registration and login via ASP.NET Core Identity
- [ ] **Ticket Booking**
  - [x] Seat locking via Redis distributed lock
  - [ ] Complete order / payment flow
- [ ] **Personalized Recommendations** — Based on booking history *(Extra Task)*

> ⭐ Items marked as *Extra Task* are bonus features from the practice requirements.

---

## 🔑 Key Implementation Details

### Seat Locking
Prevents double-booking by temporarily locking selected seats in **Redis** using a distributed lock before the final order is placed. Lock TTL ensures automatic release if the booking is not completed.

### TMDB Integration
Movies can be imported directly from [The Movie Database](https://www.themoviedb.org/) by ID via the **Refit** HTTP client. Cast, posters, genres, and details are pulled automatically. Background import jobs are handled by **Hangfire**.

### Session Conflict Detection
The scheduling service checks for time-slot overlaps within the same hall before allowing a new session to be created or an existing one to be rescheduled.

### Authentication Flow
- Registration and login handled by **ASP.NET Core Identity**.
- On successful login the API returns a short-lived **Access Token** (JWT) and a long-lived **Refresh Token** stored in the database.
- The refresh endpoint rotates tokens securely.

---

## 📜 License

This project is licensed under the **MIT License**.
