# Tickets domain boundary

`Cinema.Tickets.Domain` owns `Ticket` and `TicketStatus`. Their CLR namespaces remain `Cinema.Domain.Entities` and `Cinema.Domain.Enums`; only assembly ownership changed. Ticket status transitions and the persisted `OrderId`, `SessionId`, and `SeatId` fields are unchanged.

The domain references SharedKernel for `EntityId<T>` and `DomainException`, Scheduling.Domain for `Session` and `Seat`, and Orders.Domain because `Ticket.OrderId` is `EntityId<Order>`. This is a one-way type dependency: Orders.Domain does not reference Tickets.Domain. Neither extracted domain references legacy Cinema.Domain or an application or infrastructure project.

Ticket endpoints, DTOs, mapping, and validation remain in the legacy application and Host. Order reservation, checkout, cancellation, and event handlers remain in Orders Application. `ApplicationDbContext`, Ticket and Order EF configurations, and migrations remain in Cinema.Infrastructure because the modules still share one context, schema, and migration history. No database migration or schema change is required for assembly extraction.

Assembly-qualified serialized names can change even though CLR namespaces and API contracts stay the same. Before deployment, inspect externally persisted Hangfire jobs, serialized type metadata, and external consumers for references to `Cinema.Domain` assembly names for `Ticket` or `TicketStatus`.
