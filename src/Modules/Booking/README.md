# Booking boundary

Booking Application owns the temporary seat lock/unlock commands, validators, and their request/response DTOs. Booking Infrastructure owns `RedisSeatLockingService` and its existing Redis key formats (`lock:session:{<session-guid>}:<seat-guid>` and `locked_seats:session:{<session-guid>}`; the braces around the session GUID are literal Redis hash-tag braces), Lua scripts, retry policy, and notifications. The CLR namespaces of moved types remain unchanged for API and serialized type compatibility. The lock duration remains the 10 minute `OrderConstants.SeatLockDurationMinutes` value in the transitional application; Orders uses the same value for expiration.

`ISeatLockingService`, `IApplicationDbContext`, `ICurrentUserService`, and the mixed `ITicketNotifier` remain in the transitional application. Booking Application depends on it for validation, current-user access, and the duration constant. Moving `ISeatLockingService` into Booking Application now would require the transitional application to reference Booking Application while Booking Application already references the transitional application. The host registers Booking's handlers, validators, and Redis implementation explicitly.

`SeatLock` and its EF configuration remain in legacy Domain/Infrastructure. No runtime code reads or writes this entity; it is tied to legacy User, Session, and Seat relationships and the shared migration model. Active temporary reservations use Redis. No schema or migration changed.

Order creation and cancellation commands, order history queries, reservation, checkout orchestration, and order event handlers now live in Orders Application. The expiration job and ticket application features remain transitional. Scheduling retains physical seats and sessions; its session detail query consumes the transitional lock interface. The booking-aware session seats query remains transitional. The shared database context, API controllers, and SignalR hub stay in their existing assemblies. Any persisted assembly-qualified names for the moved lock commands or Redis service should be checked before deploying, although no Hangfire jobs for those types are registered in this repository.

## Real-time seat notifications (Phase 1)

The active notifier is the singleton `TicketNotificationWorker`, shared by `ITicketNotifier` and the hosted service. Its Channel queue dispatches these typed JSON SignalR events to `TicketHub.GroupName(sessionId.ToString())`:

| Event | Arguments, in order | Group |
| --- | --- | --- |
| `SeatLocked` | `sessionId`, `seatId`, `userId` (GUID strings) | `session:{sessionId}` |
| `SeatUnlocked` | `sessionId`, `seatId` (GUID strings) | `session:{sessionId}` |

Clients pass the complete `session:{sessionId}` string to `JoinSessionGroup` / `LeaveSessionGroup` and rejoin after reconnect. `/tickets` remains the frontend endpoint; the existing `/hubs/tickets` alias also remains. The Redis SignalR backplane retains the `CinemaPlatform` channel prefix. `OrderCompleted(orderId)` and `OrderFailed({ orderId, reason })` remain user-targeted. The unused legacy `ReceiveSeatStatusChange` interface member remains for compatibility; the worker does not emit it.

A new Redis lock notifies once; same-owner renewal and competing acquisitions do not notify. Single and batch unlocks notify only keys actually removed for their owner; batch `notifyClients=false` suppresses notifications. Notification failures do not roll back Redis mutations. Redis expiry does **not** automatically emit a SignalR event. The worker logs individual delivery failures and continues processing. Graceful shutdown completes the writer and waits for accepted work to drain until the host's shutdown deadline. It waits before base-service cancellation so that even a newly scheduled worker can drain. The deadline bounds waiting, not in-flight typed SignalR sends; delivery after the deadline is not guaranteed. This in-memory queue is not durable across crashes or forced shutdown, and failed sends are not retried.

**Deployment gate:** Phase 1 must not be deployed independently with the current frontend. A Redis unlock is not proof of availability: PostgreSQL tickets in `Reserved`, `Valid`, or `Used` status remain authoritative occupancy, including after successful purchase. The frontend currently removes unlocked seats from its occupied set without reconciliation. It must reconcile authoritative session availability after unlock before exposing a seat as available. Preserve existing lock releases and payment/order behavior. Phase 2 will address authoritative occupancy and lock ownership; TTL and checkout extension rules are unchanged here.

`tests/Cinema.BookingTests` distinguishes mocked Redis unit tests (`RedisSeatNotificationTests`), mocked typed-hub worker tests and production DI checks (`TicketNotificationWorkerTests`), and real hub JSON integration tests (`TicketHubIntegrationTests`). The latter uses in-memory ASP.NET Core TestServer hosts and real .NET SignalR clients, including session isolation, user targeting, leave, and fresh-connection rejoin. It does not run the complete API startup pipeline, automatic reconnect transport recovery, or a browser.

Real Redis tests (`RedisSeatLockingTests` and the two-host hub/backplane case) run when `CINEMA_TEST_REDIS_CONNECTION` contains a working connection string; otherwise they skip. They use unique session/seat keys, clean up in `finally`, and isolate the test backplane channel prefix. No production Redis keys or lock TTL settings are changed. Run with the repository's .NET 10 Microsoft.Testing.Platform runner:

```sh
dotnet test --project tests/Cinema.BookingTests/Cinema.BookingTests.csproj -c Release --no-build -- --fail-skips on --timeout 2m
```
