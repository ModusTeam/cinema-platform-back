# GOLD checkout compensation

## Required invariant

Before sending UseGoldUpgrade, persist the user/order identity and original order and ticket prices. A GOLD checkout can finish only as a paid order retaining its upgrade, or as a failed attempt whose reservation is eventually released. Cancellation, exceptions and process termination must leave recoverable state. Until rollback is confirmed, keep the discounted prices and the recovery intent. Restore the original prices atomically with completion of the compensation. Known failed checkouts also refund all valid tickets in that commit, making the seats reservable again.

## Failure cases and approach

The existing flow rolls GOLD back only after a payment decline. Discount resolution, point deduction, post-Use exceptions and confirmation failures can bypass that branch. RPC timeouts currently become an ordinary failure even though Use may have committed remotely.

Keep a durable journal on the order, written before Use. Serialize checkout and recovery per order with a PostgreSQL session advisory lock; releasing the database connection after process death allows recovery without guessing a lease timeout. A periodic worker scans incomplete journals in batches of 100 every 30 seconds. Failed attempts remain due for retry after one minute. Each Use/rollback RPC has a five-second deadline. Per-order checkout and recovery suppress nested EF retries; connection loss cancels the attempt, and a direct probe checks the original connection before charging. The normal EF retry configuration remains available to other operations. Recovery replays an ambiguous Use with the original identity before rolling it back. A confirmed rejection has no reservation to release. A released or failed GOLD attempt cannot be reused for another charge or local discount. Before calling payment, persist `GoldPaymentOutcomeUnknown`. Clear it only on a definitive decline, an acknowledged refund, or the committed Paid state. If payment remains ambiguous, GOLD is still compensated and prices restored, but the order stays Pending and its tickets stay Valid for manual payment/points reconciliation. Expiration and explicit cancellation cannot release these flagged tickets. This includes process termination during the payment call.

Use and rollback are idempotent on the existing loyalty userId/orderId contract. No proto or loyalty-service change is required. A lost Use response gets one immediate retry with the same identifiers; unresolved transport failures retain the journal for recovery.

## Verification plan

Write regression tests before implementation for payment decline, deduction failure, discount failure, exceptions after Use, cancellation, confirmation failure, lost RPC response and repeat checkout. Verify durable state using a fresh context and PostgreSQL tests for process-recovery and locking. Run Orders tests, the full solution test suite, solution build and the available neighboring loyalty checkout harness. Keep local, mocked and real-service evidence separate.

## Limits

GOLD recovery does not make payment or points compensation durable. The payment provider contract has no query-by-order or idempotent charge operation, so crashes after a charge but before local confirmation still need payment reconciliation. No exactly-once guarantee is claimed. A database migration is required before running code that writes the journal; do not deploy as part of this task.

## Verification commands

Use .NET 10 on PATH (including subprocesses started by DbUpdater tests). For the full suite, set `CINEMA_TEST_POSTGRES_CONNECTION` to an isolated pgvector PostgreSQL connection with a `cinema_orders_test_*` database name and `CINEMA_TEST_REDIS_CONNECTION` to isolated Redis. Run:

```powershell
dotnet test tests/Cinema.OrdersTests/Cinema.OrdersTests.csproj
dotnet test Cinema.sln
dotnet build Cinema.sln
bun tests/checkout-e2e/run.mjs
```

The checkout harness requires a clean, committed loyalty checkout with the synchronized GOLD transport assertions, installed Bun dependencies and Docker Compose. By default it uses `../cinema-loyalty-service`; set `LOYALTY_REPO` to select an isolated worktree. Jest executes that repository's original `test/e2e/checkout.transport-spec.ts` directly through its existing config: no source copying, assertion replacement or test renaming. Both repositories must be clean so `.tools/e2e/transports-versions.json` identifies the exact tested commits. The suite checks declined GOLD prices at the original 600, Refunded tickets, HTTP seat availability, and a new paid GOLD order on the same seat. Synthetic order/journal/ticket/reservation/ledger correlations are saved in `.tools/e2e/checkout-evidence.json`; failed HTTP responses expose an error code/traceId without orderId, so the failure is correlated through its unique seeded user/session/seat and reservation. JSON results are in `.tools/e2e/transports-results.json`. Docker resources use random loopback ports and disposable databases; the runner removes only its own Compose project. Do not run it concurrently with the loyalty transport harness, which shares the local monolith image tag.

```powershell
$env:LOYALTY_REPO='../cinema-loyalty-integration' # optional clean worktree
bun tests/checkout-e2e/run.mjs
```

PostgreSQL tests cover journals abandoned before and after confirmed Use, repeat recovery, active checkout versus recovery, and forced `pg_terminate_backend` during Use and discount calculation with production-style EF retries enabled. Known failures verify seat resale; ambiguous-payment recovery verifies the seat remains held and blocks expiration/cancellation. RPC response-loss tests inject `DeadlineExceeded`/`Unavailable` at the generated client boundary; these are unit tests, not real network fault injection. The live checkout suite covers HTTP, real loyalty gRPC, PostgreSQL, RabbitMQ, Redis outage/restart, points refunds and GOLD rollback with restored prices.

Session advisory locks require direct PostgreSQL connections or session pooling; transaction-mode pooling is unsuitable. Pre-existing GOLD checkouts have no journal or original price snapshot and are not automatically backfilled. Reconcile those separately before rollout. Repeated permanent compensation errors retain the journal and emit error logs; operators must investigate persistent failures. Payment/points crash recovery, domain-event delivery, and the gap between saving an order and publishing TicketPurchased remain separate existing limitations.
