# Booking, order, and ticket lifecycle (Phase 3)

## Current path and compatibility boundary

Redis holds a temporary, user-owned seat lock for ten minutes. `CreateOrderCommand` checks those locks, then `OrderReservationService` writes a `Pending` order and `Reserved` tickets in one PostgreSQL transaction. The partial unique ticket index is the durable seat uniqueness guard. The reservation retains the existing order and ticket IDs and price snapshots.

Checkout validates ownership and requires every ticket to be `Reserved`. A conditional PostgreSQL update claims `Pending -> PaymentProcessing`; only the request that updates one row may call the payment provider. This status is committed before the provider call, with no database transaction held across that call. A confirmed success saves `Paid` and `Valid` together before publishing the existing purchase event. A definitive decline saves `Failed` and `Released` together. A provider exception or explicit unknown result saves `PaymentOutcomeUnknown` and keeps tickets `Reserved`. Repeated checkout of a paid order returns the order ID; processing, unknown, failed, cancelled, and expired orders cannot start another charge. The in-memory test provider uses the equivalent domain transition for unit tests; PostgreSQL is authoritative in production.

Expiration queries only old `Pending` orders whose tickets are all `Reserved`. Each order is conditionally claimed as `Expired` inside a PostgreSQL transaction, then its tickets are released in the same transaction. After commit, the job uses the existing Redis unlock and SignalR seat notification interfaces. A concurrent checkout can claim the pending state first, in which case expiration does nothing; if expiration claims first, checkout cannot charge. Processing and unknown orders remain reserved until reconciled, even when the Redis lock expires. A process crash after the claim can leave `PaymentProcessing` indefinitely; there is no safe automatic timeout recovery with the current payment abstraction.

Pending cancellation conditionally claims `Cancelled` and releases its reserved tickets in one PostgreSQL transaction. Paid cancellation retains the existing payment, points, and GOLD refund sequence, then marks valid tickets refunded. Cancellation of processing or uncertain orders is rejected. Repeated cancellation cannot refund released or refunded tickets. The existing paid cancellation external refund and database write remain separate operations and still require the #54 payment design.

Availability, booking validation, session occupancy, and session or seat changes treat `Reserved`, `Valid`, and `Used` as blocking. `Released` and `Refunded` remain historical rows and do not block resale. Analytics continues to count confirmed purchases only. Admission rejects reserved and released tickets. Order history includes expired orders in the past bucket and retains historical tickets.

## Target ownership and transitions

Redis locks are short lived admission hints. A `Ticket` in `Reserved` status is the durable reservation owned by `OrderId`; it is not a purchased ticket. `Valid` means confirmed purchase, `Used` means admission, `Refunded` means a purchase was refunded, and `Released` means an unpaid reservation ended. Historical rows remain. `PaymentProcessing` and `PaymentOutcomeUnknown` retain the reservation until a definitive outcome is established. No separate reservation aggregate is needed because a ticket already carries order, session, and seat identity and has its own historical row.

| Order | Ticket | Meaning |
| --- | --- | --- |
| Pending | Reserved | New, unpaid reservation. |
| PaymentProcessing | Reserved | Checkout claimed; provider outcome pending. |
| PaymentOutcomeUnknown | Reserved | Provider outcome needs reconciliation. |
| Paid | Valid or Used | Confirmed purchase; Used has passed admission. |
| Failed or Expired | Released | Unpaid reservation ended. |
| Cancelled | Released | Pending reservation cancelled. |
| Pending | Valid | Legacy ambiguity; seat remains blocked until payment reconciliation. |
| Failed or Cancelled | Refunded | Historical refunds remain visible but do not block resale. |

| Order from | Allowed next state | Condition |
| --- | --- | --- |
| Pending (0) | PaymentProcessing (4), Failed (2), Expired (6), Cancelled (3) | Reserve before charging; expire only before a payment attempt starts. |
| PaymentProcessing (4) | Paid (1), Failed (2), PaymentOutcomeUnknown (5) | A definitive payment result or uncertainty. |
| PaymentOutcomeUnknown (5) | Paid (1), Failed (2) | Reconciliation establishes a definitive result. |
| Paid (1) | Cancelled (3) | Successful refund and compensation are coordinated by the application. |
| Failed (2), Cancelled (3), Expired (6) | None | Terminal; retry of the same transition is harmless. |

`Pending -> Paid` remains a domain compatibility bridge for legacy setup and reconciliation callers; the checkout orchestrator does not use it. Payment confirmation requires a nonempty transaction ID; a repeat with the same ID is harmless and a different ID is rejected. A paid order cannot expire. The domain rejects cancellation or expiration while payment is processing or uncertain. The `Failed` transition is valid only after a definitive failure; the domain cannot determine whether a provider outcome is definitive. A precharge validation failure may reset `PaymentProcessing -> Pending` only before invoking the provider.

| Ticket from | Allowed next state | Condition |
| --- | --- | --- |
| Reserved (3) | Valid (0), Released (4) | `ConfirmPurchase` requires the owning order to be `Paid`; release requires the owning order to be `Failed`, `Expired`, or `Cancelled`. |
| Valid (0) | Used (1), Refunded (2) | Purchased ticket. |
| Used (1), Refunded (2), Released (4) | None | Historical rows remain. |

Repeated confirmation, release, and refund calls are harmless when already in their destination state. The current `Ticket.New(..., Valid, ...)` factory remains for legacy callers and is the outstanding bypass of the purchase guard. Admission additionally requires the owning order to be `Paid`, so a legacy `Pending`/`Valid` ticket cannot be used before reconciliation.

## Persistence and rollout

The unique `(session_id, seat_id)` partial index covers statuses `Reserved (3)`, `Valid (0)`, and `Used (1)`. PostgreSQL rejects simultaneous reservations or purchases, including mixed legacy/new rows. `Released (4)` and `Refunded (2)` stay outside the index, enabling resale while retaining history. Redis expiry alone never releases durable occupancy. The Phase 1 migration only replaces this index; it keeps all tables, IDs, status values, and existing ticket rows. Existing `Pending`/`Valid` pairs may represent payment in flight or paid at the provider. Checkout, expiration, and pending cancellation leave them unchanged and require authoritative payment reconciliation. Paid/Valid or Paid/Used historical tickets remain valid purchases; Failed or Cancelled/Refunded tickets remain historical.

Deploy the index migration before the Phase 2 application writes `Reserved`. The migration does not rewrite existing rows. New statuses are appended, preserving numeric values 0–3 for orders and 0–2 for tickets. The REST DTO shapes and existing RabbitMQ and SignalR payloads are unchanged, although order history can now contain the new status names. Consumers of enum names should account for them.

The DbUpdater migration test applies the previous schema, inserts a legacy `Pending`/`Valid` pair, runs the updater, and verifies IDs, numeric statuses, the new index, and a conflicting reservation rejection. The migration's `Down` method restores the old partial index over `Valid` and `Used` only. A rollback after new `Reserved` rows exist therefore removes their database uniqueness protection; roll back application writes or reconcile reservations before using the old schema. No migration converts or removes historical rows. `Pending`/`Valid` remains seat-blocking and visible in history and details, but cannot be checked out, cancelled as an unpaid reservation, expired, or admitted. Its payment outcome must be established from authoritative provider records before any status change.

## Application boundary and concurrency

`POST /api/orders` currently creates the reservation and immediately attempts checkout in one request. There is no separate public checkout route. `GET /api/orders` and `GET /api/tickets/{id}` expose status names as strings in their DTOs. Owner checks protect details and cancellation; ticket admission is admin-only. New status names extend the values clients may see, while the route and payload shapes remain unchanged.

The repository has handler, service, PostgreSQL, and Redis integration coverage for these paths, but no test host for the complete ASP.NET Core pipeline. Route, serialization, and authorization statements above are based on controller and DTO inspection; they are not claims of HTTP-level integration coverage.

PostgreSQL conditional updates serialize claims on the same `Pending` order: checkout, expiration, and pending cancellation cannot all win. Expiration and pending cancellation commit the order status and ticket releases in one transaction. Checkout commits `PaymentProcessing` before external calls, then saves `Paid`/`Valid` or `Failed`/`Released` together. The partial unique index remains the final seat ownership guard, including legacy `Valid` and `Used` rows. EF change tracking is cleared after checkout's conditional update; expiration reloads after its claim. These guarantees cover local PostgreSQL state, not external effects.

## Payment interruption and #54 handoff

The existing `IPaymentService` has no durable attempt identity, idempotent charge key, or outcome lookup. A crash after persisting `PaymentProcessing` but before the provider call leaves a held reservation with an unstarted payment indistinguishable from an interrupted call. A crash after the provider receives the request but before its response leaves the charge outcome unknown. A crash after provider success but before final database persistence can leave a real charge without `Paid`/`Valid`. Checkout marks a failed confirmation `PaymentOutcomeUnknown` when it can; if that save also fails, the database may remain `PaymentProcessing`. Neither state is automatically retried or expired.

GOLD and points calls lack a shared transaction with PostgreSQL. Their order-scoped keys and persisted ticket flags reduce normal retry duplication, but an interruption between an external call and persistence can still require reconciliation. Paid cancellation refunds before its database save, so a retry after interruption may repeat a provider refund request. Existing pre-save domain-event dispatch can publish external effects before a database rollback. No exactly-once guarantee is claimed. Issue #54 must provide durable payment attempt identity, provider idempotency and outcome lookup, reconciliation of processing and unknown orders and legacy `Pending`/`Valid` rows, refund coordination, and durable event publication.
