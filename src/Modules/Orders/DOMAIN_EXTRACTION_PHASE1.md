# Orders and Tickets domain extraction: Phase 1 boundary

This phase removes `Order.User`, the only direct legacy entity navigation on `Order`. `Order.UserId` remains a `Guid`. `OrderConfiguration` still maps its foreign key to `User` with restrict deletion and the existing `UserId` index. The database table and migration history are unchanged.

## Planned ownership (no types moved in Phase 1)

| Planned assembly | Current files and types |
| --- | --- |
| `Cinema.Orders.Domain` | `src/Cinema.Domain/Entities/Order.cs` (`Order`); `Enums/OrderStatus.cs` (`OrderStatus`); `Events/OrderPaidEvent.cs`, `OrderFailedDomainEvent.cs`, `OrderCancelledDomainEvent.cs` (the three order events) |
| `Cinema.Tickets.Domain` | `src/Cinema.Domain/Entities/Ticket.cs` (`Ticket`); `Enums/TicketStatus.cs` (`TicketStatus`) |
| Legacy `Cinema.Domain` | `Entities/User.cs` (`User`), `RefreshToken.cs`, `SeatLock.cs`, `PaginatedList.cs`; `Enums/OccupancyStatus.cs`; `Exceptions/PriceNotConfiguredException.cs`; `Services/OrderingDomainService.cs` until final cleanup |
| `Cinema.SharedKernel` | Existing `Common/BaseEntity.cs`, `Common/EntityId.cs`, `Shared/Result.cs`, `Shared/Error.cs`, `Interfaces/IDomainEvent.cs`, `Exceptions/DomainException.cs` |
| Shared `Cinema.Infrastructure` | `Persistence/Configurations/OrderConfiguration.cs`, `TicketConfiguration.cs`, `UserConfiguration.cs`, `SeatLockConfiguration.cs`; `ApplicationDbContext.cs`; all existing migrations and model snapshot |
| `Cinema.Orders.Application` | Existing order event handlers, commands, queries, DTO, mapping, reservation, checkout and GOLD services; no domain event handler moves to either Domain assembly |

The event records retain their `Order` payloads and `Cinema.Domain.Events` CLR namespace. Handlers remain in `src/Modules/Orders/Cinema.Orders.Application/EventHandlers/`: `OrderPaidEventHandler`, `OrderPaidIntegrationEventHandler`, `OrderFailedEventHandler`, and `OrderCancelledEventHandler`. `DispatchDomainEventsInterceptor` remains in `Cinema.Infrastructure`. Its dispatch timing is unchanged.

## Dependency graph for extraction

`Orders.Domain -> SharedKernel, Scheduling.Domain`. `Tickets.Domain -> SharedKernel, Orders.Domain, Scheduling.Domain`. `Scheduling.Domain -> SharedKernel, Catalog.Domain`; `Catalog.Domain -> SharedKernel`. No new Domain assembly should reference `Cinema.Domain`. Keep `EntityId<Order>` on `Ticket.OrderId`; the one-way Tickets-to-Orders reference supports it without a shared identifier type. Keep `Order.Session`, `Ticket.Session`, and `Ticket.Seat` because their entity types already live in `Scheduling.Domain` and create no cycle.

Moving the types will require explicit project references from the transitional `Cinema.Domain` (while `OrderingDomainService` remains), `Cinema.Application`, `Cinema.Infrastructure`, `Cinema.Orders.Application`, and relevant tests/Host projects as their direct type use requires. Avoid relying on transitive references for direct use. `IApplicationDbContext` and `ApplicationDbContext` keep their `DbSet<Order>` and `DbSet<Ticket>` members. `OrderConfiguration` and `TicketConfiguration` keep their shared-context relationships; neither configuration belongs in a Domain assembly. Architecture tests need the two new assemblies and one-way dependency rules in the extraction phase.

`OrderingDomainService.CreateOrderAggregate` is only declared in its own file: no production registration, constructor injection, call, or test reference was found. It depends on `User`, `Order`, `Ticket`, both statuses, and Scheduling entities, so it cannot move into either target Domain assembly under the graph above. It can be removed during final cleanup after a final call-site check; it is retained in Phase 1.

## Compatibility checks for Phase 2

Preserve `Cinema.Domain.Entities`, `Cinema.Domain.Enums`, and `Cinema.Domain.Events` CLR namespaces when moving files. Assembly-qualified names will still change from `Cinema.Domain` to `Cinema.Orders.Domain` or `Cinema.Tickets.Domain`; check persisted serialized type names, Hangfire records, and external consumers before deployment. The repository has no Hangfire registration for `Order`, `Ticket`, or the order events, but external persisted jobs cannot be ruled out. EF migration designer files and `ApplicationDbContextModelSnapshot` contain string entity names and navigation metadata; compare the runtime model and pending-model-changes result after each move. Do not regenerate historical migrations or change SQL solely for assembly ownership.
