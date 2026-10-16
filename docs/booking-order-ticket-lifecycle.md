# Booking, order, and ticket lifecycle (Phase 1)

## Current path and compatibility boundary

Redis holds a temporary, user-owned seat lock for ten minutes. `CreateOrderCommand` checks those locks, then `OrderReservationService` writes a `Pending` order and `Valid` tickets in one PostgreSQL transaction. The ticket index, rather than Redis, is the durable seat uniqueness guard. Checkout applies optional GOLD and loyalty changes, calls the payment abstraction, then marks the order `Paid`; a definitive decline marks it `Failed` and refunds the `Valid` tickets. The expiration job cancels old `Pending` orders and refunds their tickets. Explicit cancellation refunds paid purchases and also accepts pending orders. Availability queries treat `Valid` and `Used` as occupied. Existing tests characterize these flows.

This means an unpaid reservation looks like a purchased ticket. A payment call can be in progress while the order is still `Pending`, so expiration or another checkout can race it. An exception from the payment service returns an unknown outcome without persisting that fact. Refund/confirmation and side effects can diverge because payment, loyalty, database saves, and message publication do not share a durable transaction. See `backend-stabilization-audit.md` for the broader findings.

Phase 1 adds the domain vocabulary and database constraint. The existing application path continues to write legacy `Pending`/`Valid` pairs until Phase 2 switches reservation, checkout, expiration, cancellation, and reads together. No new status is emitted by the current REST or messaging path in Phase 1. The domain's existing `Ticket.New` factory and direct `Pending -> Paid` order transition remain compatibility bridges for that path; they must be removed or narrowed once all callers use the new transitions. Consequently the target invariants are modeled and tested but are **not yet enforced end to end** in the running checkout flow.

## Target ownership and transitions

Redis locks are short lived admission hints. A `Ticket` in `Reserved` status is the durable reservation owned by `OrderId`; it is not a purchased ticket. `Valid` means confirmed purchase, `Used` means admission, `Refunded` means a purchase was refunded, and `Released` means an unpaid reservation ended. Historical rows remain. `PaymentProcessing` and `PaymentOutcomeUnknown` retain the reservation until a definitive outcome is established. No separate reservation aggregate is needed because a ticket already carries order, session, and seat identity and has its own historical row.

| Order from | Allowed next state | Condition |
| --- | --- | --- |
| Pending (0) | PaymentProcessing (4), Failed (2), Expired (6), Cancelled (3) | Reserve before charging; expire only before a payment attempt starts. |
| PaymentProcessing (4) | Paid (1), Failed (2), PaymentOutcomeUnknown (5) | A definitive payment result or uncertainty. |
| PaymentOutcomeUnknown (5) | Paid (1), Failed (2) | Reconciliation establishes a definitive result. |
| Paid (1) | Cancelled (3) | Successful refund and compensation are coordinated by the application. |
| Failed (2), Cancelled (3), Expired (6) | None | Terminal; retry of the same transition is harmless. |

`Pending -> Paid` remains temporarily permitted for legacy checkout. Payment confirmation requires a nonempty transaction ID; a repeat with the same ID is harmless and a different ID is rejected. A paid order cannot expire. The domain rejects cancellation or expiration while payment is processing or uncertain. The `Failed` transition is valid only after a definitive failure; the domain cannot determine whether a provider outcome is definitive.

| Ticket from | Allowed next state | Condition |
| --- | --- | --- |
| Reserved (3) | Valid (0), Released (4) | `ConfirmPurchase` requires the owning order to be `Paid`; release requires the owning order to be `Failed`, `Expired`, or `Cancelled`. |
| Valid (0) | Used (1), Refunded (2) | Purchased ticket. |
| Used (1), Refunded (2), Released (4) | None | Historical rows remain. |

Repeated confirmation, release, and refund calls are harmless when already in their destination state. The current `Ticket.New(..., Valid, ...)` factory remains for legacy callers and is the outstanding bypass of the purchase guard.

## Persistence and rollout

The unique `(session_id, seat_id)` partial index now covers statuses `Reserved (3)`, `Valid (0)`, and `Used (1)`. PostgreSQL therefore rejects two simultaneous reservations or purchases, including mixed legacy/new rows. `Released (4)` and `Refunded (2)` stay outside the index, enabling resale while retaining history. Redis expiry alone never releases this durable occupancy. The generated EF migration only replaces this index; it keeps all tables, IDs, status values, and existing ticket rows. Existing `Pending`/`Valid` pairs cannot safely be converted blindly: some may be paid at the provider or in flight while the database still says pending. They remain in the active index. Before Phase 2 rollout, reconcile those rows against authoritative payment records, then migrate each proven unpaid pending ticket to `Reserved` or `Released` in the same transaction as its order transition. Preserve paid purchases as `Valid`.

Deploy the index migration before any application writes `Reserved`. The migration does not rewrite existing rows. The new statuses are appended, preserving numeric values 0–3 for orders and 0–2 for tickets. External enum-name serialization still needs a consumer plan when Phase 2 begins returning the new states through order history or seat views; no DTO or transport format changes in Phase 1.

## Phase 2 and #54 boundary

Phase 2 must persist `PaymentProcessing` before invoking the provider, fence concurrent checkout/expiry/cancellation through a database claim or concurrency token, persist `PaymentOutcomeUnknown` on ambiguous outcomes, and atomically change each order and its tickets on definitive success/failure/expiry. It must update availability, validators, history, event handlers, and cancellation to understand `Reserved` and `Released`. A processing or uncertain order must retain its reservation regardless of elapsed Redis TTL. The existing `IPaymentService` has no durable attempt identity, idempotent charge key, or outcome lookup, so a crash between the external charge and the database write cannot be resolved safely today. #54 / Cinema.Payments must define that reconciliation, provider idempotency, refund handling, and durable publication. Until then, neither expiration safety nor exactly-once external effects can be claimed.
