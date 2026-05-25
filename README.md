# 🎬 Cinema Platform API

Backend API for a Cinema Management System developed as part of the **SoftServe Practice**. This solution provides a comprehensive RESTful API for managing movies, sessions, halls, and ticket bookings using Clean Architecture principles.

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat&logo=dotnet)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-336791?style=flat&logo=postgresql)
![Redis](https://img.shields.io/badge/Redis-Cache-DC382D?style=flat&logo=redis)
![RabbitMQ](https://img.shields.io/badge/RabbitMQ-Messaging-FF6600?style=flat&logo=rabbitmq)
![Supabase](https://img.shields.io/badge/Supabase-Database-3ECF8E?style=flat&logo=supabase)
![SignalR](https://img.shields.io/badge/SignalR-RealTime-512BD4?style=flat)
![Docker](https://img.shields.io/badge/Docker-Compose-2496ED?style=flat&logo=docker)
![License](https://img.shields.io/badge/License-MIT-green)

> **Frontend:** Check out [cinema-platform-front](https://github.com/stkossman/cinema-platform-front) for the React + TypeScript client.

---

## 🏗️ Architecture & Tech Stack

The project follows **Clean Architecture** combined with the **CQRS** (Command Query Responsibility Segregation) pattern with **Event-Driven Architecture**.

| Layer | Technologies |
|---|---|
| **API** | ASP.NET Core 8 Web API, Swagger/OpenAPI, SignalR |
| **Application** | MediatR (CQRS), FluentValidation, Mapster, Domain Events |
| **Domain** | Entities, Value Objects, Enums, Domain Exceptions, Result<T> pattern |
| **Infrastructure** | EF Core, PostgreSQL (Supabase), Redis, Refit (TMDB, Gemini), gRPC, Hangfire, Serilog |
| **Messaging** | RabbitMQ, MassTransit (Event Bus) |
| **Email** | SMTP (Gmail/custom), HTML templates, PDF attachments |
| **Auth** | ASP.NET Core Identity, JWT (Access + Refresh Tokens) |
| **Patterns** | Idempotency, Outbox, Repository, Unit of Work, Event-Driven |
| **DevOps** | Docker & Docker Compose |

---

## 📂 Project Structure

```
Cinema.Api/                # Controllers, Middleware, SignalR Hubs, Entry Point
├── Controllers/           # Account, Auth, Genres, Halls, Movies, Orders, Pricings, etc.
├── Hubs/                 # TicketHub (SignalR real-time notifications)
├── ExceptionHandlers/    # GlobalExceptionHandler
├── Middleware/           # RequestLogContextMiddleware
└── Services/             # CurrentUserService, SignalRTicketNotifier, TicketNotificationWorker

Cinema.Application/        # Business Logic, CQRS Handlers, DTOs, Validators
├── Account/              # Profile & password commands/queries
├── Achievements/         # Achievements logic & queries
├── Auth/                 # Login, Register, RefreshToken
├── Genres/               # Genre CRUD
├── Halls/                # Hall CRUD + technologies management
├── Loyalty/              # Loyalty system integration and rules
├── Movies/               # Movie CRUD + TMDB import + AI embeddings
├── Orders/               # CreateOrder, CancelOrder, GetMyOrders, ValidateTicket
│   ├── EventHandlers/    # OrderPaidIntegrationEventHandler (publishes to RabbitMQ)
│   └── Services/         # OrderCheckoutOrchestrator for complex checkout sagas
├── Pricings/             # Pricing CRUD + price calculation
├── Seats/                # Seat types, locking logic
├── Sessions/             # Session scheduling, reschedule, cancel
├── Stats/                # Dashboard KPIs and analytics
├── Technologies/         # Hall technologies (IMAX, 3D, Dolby Atmos)
├── Users/                # Role management
├── Jobs/                 # CancelExpiredOrdersJob (Hangfire recurring job)
└── Common/
    ├── Behaviours/       # ValidationBehavior, IdempotencyBehavior
    ├── Contracts/        # TicketPurchasedMessage (message contract)
    ├── Interfaces/       # IPaymentService, IPriceCalculator, ITicketNotifier, IEmailService, etc.
    └── Mappings/         # Mapster configuration

Cinema.Domain/             # Core: Entities, Value Objects, Enums, Events
├── Entities/             # Movie, Hall, Session, Seat, Order, Ticket, Genre, Pricing, etc.
├── Enums/                # MovieStatus, SeatStatus, SessionStatus, OrderStatus, TicketStatus
├── Events/               # OrderPaidEvent (domain events)
└── Shared/               # Result<T>, Error, EntityId<T>

Cinema.Infrastructure/     # Data, Caching, External APIs, Identity, Messaging
├── Persistence/
│   ├── Configurations/   # EF Core entity configurations
│   ├── Migrations/       # Database migrations
│   └── ApplicationDbContext.cs
├── Messaging/
│   └── Consumers/        # TicketPurchased, TierUpgraded, PointsExpiring consumers
└── Services/             # Identity, Token, Redis SeatLocking, TMDB, Gemini, gRPC (Loyalty/Achievements), Payment, Email, etc.
```

---

## 🚀 Getting Started

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop)
- **Supabase** account (for PostgreSQL) *or* local PostgreSQL
- **Redis** instance
- **RabbitMQ** instance (for email notifications)
- **SMTP Server** (Gmail or other) for email delivery

### Option 1 — Docker Compose *(Recommended)*

Spins up the API, Redis, Redis Commander, and RabbitMQ:

1. Create a `.env` file in the root directory:
```env
SUPABASE_CONNECTION_STRING=Host=<your-supabase-host>;Database=postgres;Username=postgres;Password=<your-password>
REDIS_PASSWORD=your-redis-password
JWT_SECRET=your-super-secret-jwt-key-min-32-chars
RABBITMQ_HOST=rabbitmq
RABBITMQ_USER=admin
RABBITMQ_PASSWORD=admin123
SMTP_HOST=smtp.gmail.com
SMTP_PORT=587
SMTP_USERNAME=your-email@gmail.com
SMTP_PASSWORD=your-app-password
SMTP_SENDER_EMAIL=your-email@gmail.com
```

2. Run:
```bash
docker-compose up -d --build
```

The API will be available at `http://localhost:5000` and `https://localhost:5001`.
RabbitMQ Management UI will be available at `http://localhost:15672` (admin/admin123).

### Option 2 — Run Locally

1. Clone the repository:
```bash
git clone https://github.com/your-username/cinema-platform-backend.git
cd cinema-platform-backend
```

2. Update `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=cinema;Username=postgres;Password=postgres",
    "RedisConnection": "localhost:6379"
  },
  "JwtSettings": {
    "Secret": "your-32-char-secret-key",
    "Issuer": "CinemaApi",
    "Audience": "CinemaClient"
  },
  "RabbitMQ": {
    "Host": "localhost",
    "Username": "guest",
    "Password": "guest"
  },
  "SmtpSettings": {
    "Host": "smtp.gmail.com",
    "Port": 587,
    "Username": "your-email@gmail.com",
    "Password": "your-app-password",
    "SenderEmail": "your-email@gmail.com"
  }
}
```

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
- [x] **Session Management** — Schedule sessions, detect overlaps, manage pricing policies
- [x] **Hall Management** — Configure halls, seat layouts, and technologies (IMAX, 3D, etc.)
- [x] **Genre Management** — CRUD operations for movie genres
- [x] **Pricing Management** — Create dynamic pricing policies with seat type multipliers
- [x] **Sales Statistics** — View sales stats and key metrics (KPIs dashboard)
- [x] **AI Integration** — Movie embeddings for semantic search
- [x] **Loyalty Program** — Manage users' points, VIP statuses, and view balances
- [x] **Achievements** — Create and manage platform achievements

### 👤 Client

- [x] **Browse Offers** — View current movies and new releases
- [x] **Schedule Filtering** — View sessions with filters by date, time, and genre
- [x] **Movie Details** — Description, trailers, cast, ratings
- [x] **Authentication** — Registration and login via ASP.NET Core Identity
- [x] **Ticket Booking** — **COMPLETE ORDER FLOW IMPLEMENTED**
  - [x] Seat locking via Redis distributed lock (10-min TTL)
  - [x] Loyalty point discounts & VIP Gold Seat Upgrades
  - [x] Order creation with payment processing
  - [x] Automatic ticket generation
  - [x] Order history (active & past orders)
  - [x] Real-time ticket notifications via SignalR
  - [x] Ticket validation system
  - [x] **Email notifications with PDF tickets** 📧
- [x] **Order Management**
  - [x] View my orders
  - [x] Cancel pending orders
  - [x] Automatic expiration of unpaid orders (Hangfire job)
- [x] **Personalized Recommendations** — Based on AI embeddings and booking history
- [x] **Loyalty & Achievements** — Earn points, unlock VIP tiers, and gain achievements

---

## 🔑 Key Implementation Details

### Complete Order & Payment Flow with Email Notifications
The system now implements a **full end-to-end booking flow with asynchronous email delivery**:

1. **Seat Selection** → User browses available sessions and seats
2. **Seat Locking** → Redis distributed lock (10-min TTL) prevents double-booking
3. **Price Calculation** → Dynamic pricing based on seat type and session pricing policy
4. **Order Creation** → `CreateOrderCommand` generates order with `Pending` status
5. **Loyalty Processing** → `OrderCheckoutOrchestrator` applies points discounts or Gold upgrades via gRPC
6. **Payment Processing** → `IPaymentService` handles payment (mock implementation included)
7. **Ticket Generation** → On payment success, tickets are auto-generated with `Valid` status
7. **Real-Time Notification** → SignalR pushes ticket data to client via `TicketHub`
8. **Event Publishing** → `OrderPaidEvent` triggers `OrderPaidIntegrationEventHandler`
9. **RabbitMQ Message** → `TicketPurchasedMessage` published to message queue
10. **Email Delivery** → `TicketPurchasedConsumer` generates PDF ticket and sends email via SMTP
11. **Order Expiration** → Hangfire job `CancelExpiredOrdersJob` auto-cancels unpaid orders

**Event-Driven Architecture**: The system uses domain events and RabbitMQ for decoupled communication:
- `OrderPaidEvent` → Domain event raised when payment succeeds
- `OrderPaidIntegrationEventHandler` → Publishes `TicketPurchasedMessage` to RabbitMQ
- `TicketPurchasedConsumer` → Consumes messages, generates PDF tickets, sends emails
- `TierUpgradedConsumer` & `PointsExpiringConsumer` → Listen to external loyalty service events

**Orchestration (Saga Pattern)**: The `OrderCheckoutOrchestrator` manages complex checkout scenarios ensuring atomicity between database operations, payment processing, and gRPC calls (with compensation logic/refunds if failures occur).

**Idempotency**: Duplicate order requests are prevented via `IdempotencyBehavior` using request IDs.

### Loyalty & Achievements (Microservices Integration)
- **gRPC Integration**: Connects to an external Loyalty Service to handle user points, tiers, and achievements.
- **Resilience**: `OrderCheckoutOrchestrator` gracefully degrades if the loyalty service is unavailable.
- **Features**: 
  - Earn points on purchases.
  - Pay with points or apply "Gold Upgrades" to standard seats for VIP users.
  - Track user achievements seamlessly through the unified API.

### Email Notification System
- **Technology**: SMTP (Gmail or custom server), MassTransit, RabbitMQ
- **Flow**: Order payment → RabbitMQ message → Consumer generates PDF → Email sent
- **Features**:
  - HTML email templates
  - PDF ticket attachments with QR codes
  - Download links for tickets
  - Personalized greeting (user's name)
  - Movie, session, and seat information
- **Resilience**: Retry logic via MassTransit for failed email deliveries

### RabbitMQ Integration
- **Message Broker**: RabbitMQ with MassTransit abstraction
- **Message Contract**: `TicketPurchasedMessage` (OrderId, UserEmail, UserName, MovieTitle, SessionTime, SeatInfo, TotalPrice, DownloadUrl)
- **Consumer**: `TicketPurchasedConsumer` handles ticket generation and email sending
- **Configuration**: Automatic queue and exchange creation via MassTransit
- **Management UI**: Available at `http://localhost:15672` in Docker setup

### Seat Locking System
- **Technology**: Redis with distributed locks (StackExchange.Redis)
- **TTL**: 10 minutes (configurable in `RedisSeatLockingService`)
- **Resilience**: Polly retry policy with exponential backoff
- **Key Format**: `seat_lock:{sessionId}:{seatId}`
- **Auto-Release**: Locks expire automatically if payment isn't completed

### TMDB Integration
Movies can be imported directly from [The Movie Database](https://www.themoviedb.org/) by ID via the **Refit** HTTP client. Cast, posters, genres, and details are pulled automatically. Background import jobs are handled by **Hangfire**.

### AI-Powered Features
- **Embeddings**: Google Gemini API generates embeddings for movies
- **Semantic Search**: Find similar movies based on plot, themes, and content
- **Recommendations**: Personalized suggestions based on user preferences
- **Analytics**: AI-enhanced analytics for movie performance

### Pricing System
- **Dynamic Pricing**: Admin creates pricing policies with base prices
- **Seat Type Multipliers**: Different seat types (Standard, VIP, Premium) apply multipliers
- **Price Calculator**: `IPriceCalculator` interface calculates final ticket prices
- **Session-Level Pricing**: Each session can use a different pricing policy

### Session Conflict Detection
The scheduling service checks for time-slot overlaps within the same hall before allowing a new session to be created or an existing one to be rescheduled.

### Authentication & Authorization
- **Registration**: Users register with email/password via ASP.NET Core Identity
- **Login**: Returns short-lived **Access Token** (JWT, 60-min) + long-lived **Refresh Token** (stored in DB)
- **Token Refresh**: Secure token rotation via `/api/auth/refresh`
- **Roles**: `Admin` and `User` roles with role-based endpoint protection

### Real-Time Features (SignalR)
- **Hub**: `TicketHub` at `/hubs/ticket`
- **Events**: `TicketCreated`, `OrderStatusChanged`
- **Client Integration**: Frontend subscribes to receive instant ticket updates after payment

### Background Jobs (Hangfire)
- **CancelExpiredOrdersJob**: Runs every 5 minutes to cancel orders that remain `Pending` past expiration time
- **Dashboard**: Hangfire dashboard available at `/hangfire` (dev environment)

### Object Mapping
- **Mapster**: High-performance object-to-object mapper
- **Configuration**: Mapping profiles in `Cinema.Application/Common/Mappings/`
- **Usage**: `entity.Adapt<Dto>()` for clean, type-safe transformations

---

## 📡 API Endpoints Overview

### Movies
- `GET    /api/movies` — List movies with pagination & filters
- `GET    /api/movies/{id}` — Get movie details
- `POST   /api/movies` — Create movie (Admin)
- `PUT    /api/movies/{id}` — Update movie (Admin)
- `DELETE /api/movies/{id}` — Delete movie (Admin)
- `POST   /api/movies/import` — Import from TMDB (Admin)
- `GET    /api/movies/{id}/similar` — Get similar movies (AI-powered)

### Sessions
- `GET    /api/sessions` — List sessions with filters
- `GET    /api/sessions/{id}` — Get session details
- `POST   /api/sessions` — Create session (Admin)
- `PUT    /api/sessions/{id}/reschedule` — Reschedule (Admin)
- `PUT    /api/sessions/{id}/cancel` — Cancel session (Admin)

### Seats & Locking
- `POST   /api/seats/lock` — Lock a seat for booking (authenticated)
- `PUT    /api/seats/{id}/type` — Change seat type (Admin)

### Orders & Tickets
- `POST   /api/orders` — Create order (purchase tickets)
- `GET    /api/orders` — Get my orders
- `GET    /api/orders/{id}` — Get order details
- `PUT    /api/orders/{id}/cancel` — Cancel order
- `GET    /api/tickets/{id}` — Get ticket details
- `GET    /api/tickets/{orderId}/download` — Download PDF ticket
- `POST   /api/tickets/{id}/validate` — Validate ticket (Admin)

### Halls & Technologies
- `GET    /api/halls` — List halls
- `POST   /api/halls` — Create hall (Admin)
- `GET    /api/technologies` — List technologies (IMAX, 3D, etc.)

### Genres
- `GET    /api/genres` — List genres
- `POST   /api/genres` — Create genre (Admin)

### Loyalty & Achievements
- `GET    /api/admin/loyalty/users` — List users with loyalty tiers (Admin)
- `POST   /api/admin/loyalty/modify-points` — Modify user points (Admin)
- `POST   /api/admin/loyalty/users/{id}/vip` — Grant VIP status (Admin)
- `GET    /api/achievements/me` — Get my achievements
- `GET    /api/achievements` — List achievements (Admin)

### Pricings
- `GET    /api/pricings` — List pricing policies
- `POST   /api/pricings` — Create pricing (Admin)

### Statistics (Admin)
- `GET    /api/stats/kpi` — Get dashboard KPIs
- `GET    /api/stats/occupancy` — Get occupancy rates
- `GET    /api/stats/revenue` — Get revenue statistics

### Auth & Account
- `POST   /api/auth/register` — Register new user
- `POST   /api/auth/login` — Login (returns JWT + Refresh Token)
- `POST   /api/auth/refresh` — Refresh access token
- `GET    /api/account/profile` — Get my profile
- `PUT    /api/account/profile` — Update profile
- `PATCH  /api/account/profile/date-of-birth` — Set my date of birth (one-time)
- `POST   /api/account/change-password` — Change password

---

## 🧪 Testing

### Postman Collection
A complete automated test suite is included: **Cinema Booking Flow (Auto).postman_collection.json**

**Test Flow:**
1. Login → Get JWT token
2. Get Sessions → Find `OpenForSales` session
3. Get Hall Seats → Pick 2 `Active` seats
4. Lock Seat 1 & 2 → Redis locks with 10-min TTL
5. Create Order → Process payment, generate tickets, send email
6. Check Email → Receive PDF ticket attachment
7. Get My Orders → Verify order + save `ticketId`
8. Get Ticket Details → View ticket with QR code

**Environment Variables Required:**
- `baseUrl`: `http://localhost:5000`
- `email`: Your test user email
- `password`: Your test user password

---

## 🐳 Docker Deployment

The `docker-compose.yaml` includes:
- **cinema-api**: Main .NET 8 API container
- **redis**: Redis cache with password protection
- **redis-commander**: Web UI for Redis at `http://localhost:8081`
- **rabbitmq**: Message broker with management UI at `http://localhost:15672`

**Environment Variables** (in `.env` file):
- `SUPABASE_CONNECTION_STRING` — PostgreSQL connection string
- `REDIS_PASSWORD` — Redis password
- `JWT_SECRET` — JWT signing key (min 32 chars)
- `RABBITMQ_HOST` — RabbitMQ host
- `RABBITMQ_USER` — RabbitMQ username
- `RABBITMQ_PASSWORD` — RabbitMQ password
- `SMTP_HOST` — SMTP server host
- `SMTP_PORT` — SMTP server port
- `SMTP_USERNAME` — SMTP username
- `SMTP_PASSWORD` — SMTP password
- `SMTP_SENDER_EMAIL` — Sender email address
- `Grpc__LoyaltyServiceUrl` — URL for the external Loyalty gRPC Service

## 🔄 Database Migrations & Seeding in Production

In Production (`ASPNETCORE_ENVIRONMENT=Production`), database migrations and identity seeding are not executed automatically on API startup to ensure zero-downtime and safe deployment practices. Instead, they are run as separate steps:

### 1. Database Migrations (cinema-migrator)
Migrations are run using a separate container (e.g. `cinema-migrator`) that executes `dotnet ef database update`.

### 2. Database Seeding (cinema-seeder)
Seeding of default Identity roles (`Admin`, `User`) and the default admin user is executed via a separate one-off job container `cinema-seeder`.

#### Configuration / Environment Variables
The seeder is controlled by the following environment variables (which can be defined in your `.env` file):

- `SEED__RUN` (bool) — Enables/disables execution of the seeder. Defaults to `true` in Development and `false` in Production. It must be explicitly set to `true` in Production for seeding to run.
- `SEED__MODE` (bool) — Sets the API into CLI seeding mode. If `true` or the command line flag `--seed` is passed, the API runs seeding and exits immediately with code 0 (or 1 on error).
- `SEED_ADMIN__EMAIL` (string) — The email address of the default Admin user to create.
- `SEED_ADMIN__PASSWORD` (string) — The password for the default Admin user.
- `SEED_ADMIN__FIRSTNAME` (string, optional) — First name of the Admin user (defaults to `System`).
- `SEED_ADMIN__LASTNAME` (string, optional) — Last name of the Admin user (defaults to `Admin`).

#### Running with Docker Compose
You can run the seeding process using the `cinema-seeder` service defined in `docker-compose.yaml`:

```bash
# Start the seeder manually (it will wait for the database and exit once done)
docker compose run --rm cinema-seeder
```

Or pass variables explicitly if you want to override them:
```bash
docker compose run --rm -e SEED_ADMIN__EMAIL=admin@example.com -e SEED_ADMIN__PASSWORD=SecretPassword123! cinema-seeder
```

---

## 📧 Email Configuration

### Gmail Setup
1. Enable 2-factor authentication on your Google account
2. Generate an **App Password**: Google Account → Security → App passwords
3. Use the app password in `SMTP_PASSWORD` environment variable

### Custom SMTP Server
Configure any SMTP server in `appsettings.json`:
```json
{
  "SmtpSettings": {
    "Host": "smtp.yourserver.com",
    "Port": 587,
    "Username": "your-username",
    "Password": "your-password",
    "SenderEmail": "noreply@yourcinema.com"
  }
}
```

---

## 🔄 Event-Driven Architecture

### Domain Events
- `OrderPaidEvent` — Raised when order payment is successful
- Handled by multiple handlers:
  - `OrderPaidEventHandler` — Updates seat status
  - `OrderPaidIntegrationEventHandler` — Publishes to RabbitMQ

### Integration Events (RabbitMQ)
- `TicketPurchasedMessage` — Message contract for ticket purchases
- Published by: `OrderPaidIntegrationEventHandler`
- Consumed by: `TicketPurchasedConsumer`
- Content: Order details, user info, movie info, download URL

### Benefits
- **Decoupling**: Email sending doesn't block order processing
- **Reliability**: RabbitMQ ensures message delivery
- **Scalability**: Multiple consumers can process emails in parallel
- **Resilience**: Automatic retries for failed deliveries

---

## 📜 License

This project is licensed under the **MIT License**.
