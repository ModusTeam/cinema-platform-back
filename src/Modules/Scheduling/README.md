# Scheduling boundary

Scheduling owns sessions, halls, technologies, physical seats and seat types, pricing policies, and their EF configurations. Its domain assembly references Catalog.Domain to preserve `Session.Movie` in the shared EF model. Catalog has no direct reference to Scheduling.

The moved domain and application types keep their existing CLR namespaces while migrations, API contracts, and any external serialized type names still rely on them. Assembly ownership changed; persisted assembly-qualified names for Scheduling types should be checked before deployment if external Hangfire jobs exist. No Scheduling Hangfire jobs are registered in this repository.

`Scheduling.Application` temporarily uses the legacy `IApplicationDbContext` and booking-facing interfaces for session detail composition. The shared `ApplicationDbContext` remains in `Cinema.Infrastructure`, loads Scheduling configurations explicitly, and retains the migration assembly and database schema. The existing UTC converter now lives with Scheduling infrastructure and is reused by the shared context and legacy configurations. These dependencies should be revisited when persistence boundaries are extracted.

Booking owns temporary Redis seat locks and lock/unlock commands. Orders.Domain and Tickets.Domain own the extracted Order and Ticket types. Checkout orchestration and order expiration jobs remain transitional; Orders Application owns the extracted order commands, history queries, and event handlers. The booking-aware session seat availability query remains in the legacy application layer, and Scheduling Application continues to consume its transitional `ISeatLockingService` boundary for session detail composition. `Ticket.Session` remains mapped to `Session` with the same foreign key and delete behavior; `Session.Tickets` was removed to avoid a domain assembly cycle.

## Custom hall layouts

`POST /api/halls` accepts an optional `seats` array of `{ gridX, gridY, seatTypeId }` alongside the existing `name`, `rows`, `seatsPerRow`, default `seatTypeId`, and `technologyIds`. Omitting `seats` or sending `null` preserves legacy rectangular creation. A nonempty array creates only the supplied seats.

Admin-only `PUT /api/halls/{id}/layout` replaces the entire layout and returns `204 No Content`. The ID comes from the route; the body contains no name or technologies:

```json
{
  "rows": 5,
  "seatsPerRow": 8,
  "seats": [
    { "gridX": 2, "gridY": 1, "seatTypeId": "11111111-1111-1111-1111-111111111111" },
    { "gridX": 3, "gridY": 1, "seatTypeId": "22222222-2222-2222-2222-222222222222" },
    { "gridX": 1, "gridY": 2, "seatTypeId": "11111111-1111-1111-1111-111111111111" }
  ]
}
```

Both sparse operations share validation: nonempty, non-null seat definitions; unique positive **1-based** coordinates inside the submitted bounds; nonempty existing seat-type IDs; at most 1,000 rows, 1,000 columns, and 10,000 seats. Gaps remain absent seats. Row labels use `gridY`; numbering restarts at 1 for each row in ascending `gridX` order, independent of input order. Invalid JSON/GUIDs and validation failures return `400`; unknown halls or seat types return `404`.

Editing requires an active hall with **no persisted session history**, regardless of dates or cancellation status. History queries bypass global filters. Sessions currently have no separate soft-delete flag; any retained session row counts. Any ticket or legacy database seat-lock referencing the hall's seats also blocks editing. `Hall.LayoutLocked` returns `409 Conflict`; inactive halls return `409 Hall.Inactive`. Single and batch seat-type assignment use the same guard. Seat-type name/description maintenance and independent hall name/technology updates retain their existing behavior.

`BeginHallTransactionAsync` opens a PostgreSQL `READ COMMITTED` transaction and acquires `SELECT ... FOR UPDATE` on the hall row. Layout editing, seat-type assignment, and session creation all acquire this lock before checking and writing, holding it through commit. If creation commits first, editing sees its history and fails; if editing commits first, creation proceeds against the committed layout. The lock also conflicts with PostgreSQL's hall foreign-key key-share locks. The complete operation runs inside EF's execution strategy, with failed tracked graphs cleared before retry. This synchronization is database-backed across application instances; no Redis lock is involved.

Replacement loads and tracks original seats, validates references, generates new seats through `SeatLayoutService`, invokes encapsulated `Hall.ReplaceLayout`, explicitly marks old seats deleted and new seats added, and recalculates capacity. One save and transaction commit preserve foreign keys and atomicity. Exceptions roll back all deletes, inserts, and capacity changes. Existing hall DTOs, booking, pricing, name/technology endpoints, and legacy creation are unchanged. Dimensions remain request-only: the EF model is unchanged and **no migration is required**.

The policy covers retained history; physically purged sessions cannot be inferred without adding persisted history, which is outside this MVP. Keep operational session history intact. Direct SQL maintenance must honor the same policy.

`Cinema.SchedulingTests` includes pure validation/domain/pipeline regressions and real PostgreSQL replacement, rollback, history, seat-type and two-order concurrency tests. PostgreSQL tests reuse the existing disposable `cinema_orders_test_*` fixture via `CINEMA_TEST_POSTGRES_CONNECTION`; missing infrastructure is reported as skipped, and CI uses `--fail-skips on`.

The SonarQube job provisions PostgreSQL, Redis, and RabbitMQ as in `.NET CI`, collects Microsoft XML coverage with `dotnet-coverage` between scanner Begin/End, and imports `coverage.xml` using `sonar.cs.vscoveragexml.reportsPaths`. Test projects and generated source are excluded from production coverage metrics. The existing quality gate remains authoritative; importing a report does not itself establish 80% coverage.
