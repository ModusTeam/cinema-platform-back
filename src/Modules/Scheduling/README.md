# Scheduling boundary

Scheduling owns sessions, halls, technologies, physical seats and seat types, pricing policies, and their EF configurations. Its domain assembly references Catalog.Domain to preserve `Session.Movie` in the shared EF model. Catalog has no direct reference to Scheduling.

The moved domain and application types keep their existing CLR namespaces while migrations, API contracts, and any external serialized type names still rely on them. Assembly ownership changed; persisted assembly-qualified names for Scheduling types should be checked before deployment if external Hangfire jobs exist. SessionCompletionJob is registered as a minute-level recurring Hangfire scanner.

`Scheduling.Application` temporarily uses the legacy `IApplicationDbContext` and booking-facing interfaces for session detail composition. The shared `ApplicationDbContext` remains in `Cinema.Infrastructure`, loads Scheduling configurations explicitly, and retains the migration assembly and database schema. The existing UTC converter now lives with Scheduling infrastructure and is reused by the shared context and legacy configurations. These dependencies should be revisited when persistence boundaries are extracted.

Booking owns temporary Redis seat locks and lock/unlock commands. Orders.Domain and Tickets.Domain own the extracted Order and Ticket types. Checkout orchestration and order expiration jobs remain transitional; Orders Application owns the extracted order commands, history queries, and event handlers. The booking-aware session seat availability query remains in the legacy application layer, and Scheduling Application continues to consume its transitional `ISeatLockingService` boundary for session detail composition. `Ticket.Session` remains mapped to `Session` with the same foreign key and delete behavior; `Session.Tickets` was removed to avoid a domain assembly cycle.

## Screening end and hall occupancy

`Session.EndTime` / API `endTime` is the nullable scheduled screening end. New sessions scheduled through the service capture `StartTime + movie duration`; `OccupancyEndTime` / `occupancyEndTime` additionally includes cleaning (15 minutes for the existing create endpoint). Overlap checks, the scheduling index and the hall-deletion time guard use occupancy. Rescheduling preserves both recorded durations without consulting a possibly edited movie duration. Ordinary rescheduling rejects an original start at or before the current UTC time, including sessions whose status has not yet been updated by a scheduler. Existing ticket guards are retained. Ordinary cancellation is rejected at or after StartTime (see Authoritative session completion v1).

Migration `SeparateSessionScreeningAndOccupancy` renames the old `end_time` column to `occupancy_end_time`, preserving every recorded value and the index reference, then adds a nullable `end_time`. Old writers did not consistently include cleaning, so the migration does not subtract a guessed interval or reconstruct screening time from current movie metadata. Existing sessions retain their previous scheduling boundary and have an unknown screening end, also after rescheduling. Consumers must handle `endTime: null`; the occupancy field is not evidence of screening completion. Completion facts are emitted by the Authoritative session completion v1 workflow described below.

The schema/API change requires a coordinated backend update: stop old readers/writers, apply the reviewed migration under separate operational approval, then start the matching application version. Old binaries cannot safely use the repurposed nullable column; do not perform a mixed-version rollout. No historical backfill or shared-database migration is part of this change. `Down` discards newly recorded screening ends and is not a routine lossless rollback. PostgreSQL migration tests cover clean/prior schemas and both historical timing shapes; row-lock tests cover cleaning-gap conflicts and exact-boundary creation.

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


## Authoritative session completion v1

`SessionCompletionJob` scans up to 50 due sessions per tick in `(end_time, id)` order. A singleton keyset cursor advances even on failures and wraps on an empty page. It is a fairness hint, not durable business state: after a restart all still-eligible rows remain discoverable. A fresh scoped context is used for every attempt. Null screening ends remain unknown; inactive halls do not hide completion candidates. No paid order is required.

Completion, ordinary cancellation and rescheduling use a short READ COMMITTED transaction, Hall UPDATE then Session UPDATE locks, and a 3-second lock timeout. State is reloaded after arbitration. Ordinary cancel/reschedule lock timeouts return Session.Busy (HTTP 409); unexpected or ambiguous-commit errors remain failures, not automatic retries. Reservation takes Session SHARE before seat locks, checks availability while holding it, and does not take Hall UPDATE. This closes the demonstrated race where booking could commit after cancellation. Checkout/payment network flows are unchanged. The lifecycle execution strategy has zero callback retries; global Npgsql retries remain enabled. After an ambiguous commit, discard the scope and inspect source state from a new context; never assume rollback or replay the callback.

**Business policy awaiting explicit acceptance before merge:** ordinary cancellation is rejected at or after the original StartTime, regardless of scanner lag. Existing Reserved/Valid/Used ticket restrictions remain. Rescheduling after the original start remains prohibited. Technical interruption/corrections after start need a future audited process; this PR creates no such endpoint.

Ended transition, immutable `session_completion_facts` snapshot and scoped MassTransit EF Bus Outbox record commit together. `ISendEndpointProvider.Send` stages through `UseBusOutbox`; it does not perform broker I/O under lifecycle locks. EventId is generated once, used as transport MessageId and retained with revision 1. The raw envelope is `SESSION_COMPLETED` v1, session-scoped, with UTC `completedAtUtc` formatted to six fractional digits from persisted screening EndTime. OccupancyEndTime and worker time are never substituted. Exact source snapshots survive normal outbox retention. Normal EF update/delete of source history is rejected.

An Ended session without source evidence raises `SESSION_COMPLETION_SOURCE_MISSING` when explicitly checked; no fact is invented. Scanner failures log only SessionId and static code, never exception payloads. Inspect failures operationally; the scanner is not an audited correction mechanism.

### Rollout and replay

Apply the additive source-table/due-index migration only to the intended database, then run the producer only after the passive Loyalty consumer is present on every replica. This development project has no historical completion backfill. The sessions index is an ordinary CREATE INDEX: assess write blocking separately before applying on a large database. No production safety claim follows from disposable tests. Rolling back the schema deletes source evidence; stop the job and retain evidence before any explicitly approved rollback.

There is no operator restaging command in this slice. After transport retention cleanup the original event remains in source history, but recovery requires a separately reviewed restaging operation that reads the exact stored snapshot and retains EventId/MessageId. Do not create a new completion identity or mutate the snapshot. Future correction revisions and automated repair are excluded.

Tests include production serialization against fixtures copied verbatim from Loyalty `87b9caac7ba452eab0586ceee779b45cef491239`, real PostgreSQL lock barriers/rollback, multi-batch poison-row progression, and real RabbitMQ delivery from a restarted outbox publisher. Manual wire duplicates are not lost-ACK proof. The full cross-service E2E and process-kill/broker-restart fault matrix remain independent verification work; no new loyalty economy is activated.
