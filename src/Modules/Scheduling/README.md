# Scheduling boundary

Scheduling owns sessions, halls, technologies, physical seats and seat types, pricing policies, and their EF configurations. Its domain assembly references Catalog.Domain to preserve `Session.Movie` in the shared EF model. Catalog has no direct reference to Scheduling.

The moved domain and application types keep their existing CLR namespaces while migrations, API contracts, and any external serialized type names still rely on them. Assembly ownership changed; persisted assembly-qualified names for Scheduling types should be checked before deployment if external Hangfire jobs exist. No Scheduling Hangfire jobs are registered in this repository.

`Scheduling.Application` temporarily uses the legacy `IApplicationDbContext` and booking-facing interfaces for session detail composition. The shared `ApplicationDbContext` remains in `Cinema.Infrastructure`, loads Scheduling configurations explicitly, and retains the migration assembly and database schema. The existing UTC converter now lives with Scheduling infrastructure and is reused by the shared context and legacy configurations. These dependencies should be revisited when persistence boundaries are extracted.

Booking owns temporary Redis seat locks and lock/unlock commands. Orders, order expiration jobs, and tickets remain in the transitional application. The booking-aware session seat availability query remains in the legacy application layer, and Scheduling Application continues to consume its transitional `ISeatLockingService` boundary for session detail composition. `Ticket.Session` remains mapped to `Session` with the same foreign key and delete behavior; `Session.Tickets` was removed to avoid a domain assembly cycle.
