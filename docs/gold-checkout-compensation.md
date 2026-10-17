# GOLD checkout compensation

## Required invariant

Before sending UseGoldUpgrade, persist the user/order identity and original order and ticket prices. A GOLD checkout can finish only as a paid order retaining its upgrade, or as a failed attempt whose reservation is eventually released. Cancellation, exceptions and process termination must leave recoverable state. Until rollback is confirmed, keep the discounted prices and the recovery intent. Restore the original prices atomically with completion of the compensation.

## Failure cases and approach

The existing flow rolls GOLD back only after a payment decline. Discount resolution, point deduction, post-Use exceptions and confirmation failures can bypass that branch. RPC timeouts currently become an ordinary failure even though Use may have committed remotely.

Keep a durable journal on the order, written before Use. Serialize checkout and recovery per order with a PostgreSQL session advisory lock; releasing the database connection after process death allows recovery without guessing a lease timeout. A periodic worker scans incomplete journals in batches of 100 every 30 seconds. Failed attempts remain due for retry after one minute. Each Use/rollback RPC has a five-second deadline. Per-order checkout and recovery suppress nested EF retries; connection loss cancels the attempt, and a direct probe checks the original connection before charging. The normal EF retry configuration remains available to other operations. Recovery replays an ambiguous Use with the original identity before rolling it back. A confirmed rejection has no reservation to release. A released or failed GOLD attempt cannot be reused for another charge or local discount.

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

The checkout harness requires the adjacent `../cinema-loyalty-service` checkout, its installed Bun dependencies and Docker Compose. It reads the adjacent harness, copies its checkout tests into ignored `.tools/e2e`, and changes only the old GOLD price expectation from 400/upgraded to the original 600/not upgraded. A source-shape guard fails if that expectation changes upstream. It uses temporary Docker resources with random loopback ports, runs migrations only against those disposable databases, and cleans them up. It does not write files to the loyalty repository. JSON test results are kept locally in `.tools/e2e/transports-results.json`.

PostgreSQL tests cover journals abandoned before and after confirmed Use, repeat recovery, active checkout versus recovery, and forced `pg_terminate_backend` during Use and discount calculation with production-style EF retries enabled. RPC response-loss tests inject `DeadlineExceeded`/`Unavailable` at the generated client boundary; these are unit tests, not real network fault injection. The live checkout suite covers HTTP, real loyalty gRPC, PostgreSQL, RabbitMQ, Redis outage/restart, points refunds and GOLD rollback with restored prices.

Session advisory locks require direct PostgreSQL connections or session pooling; transaction-mode pooling is unsuitable. Pre-existing GOLD checkouts have no journal or original price snapshot and are not automatically backfilled. Reconcile those separately before rollout. Repeated permanent compensation errors retain the journal and emit error logs; operators must investigate persistent failures. Payment/points crash recovery and precommit event publication remain separate existing limitations.
