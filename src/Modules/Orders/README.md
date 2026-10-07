# Orders application boundary

`Cinema.Orders.Application` owns order creation and cancellation commands, order history queries, the order DTO and mapping, and the current order domain-event handlers. Moved types keep their existing CLR namespaces. The Host registers the assembly with `AddOrdersApplication()`.

This is an application-only extraction. `Order`, `Ticket`, their statuses and domain events, EF configurations, the shared `ApplicationDbContext`, and migrations remain in the legacy layers. `TicketDto` and its mapping remain in the legacy application because ticket detail uses them.

Reservation, checkout orchestration, GOLD pricing, checkout and loyalty previews, shared application interfaces, and `CancelExpiredOrdersJob` also remain transitional. The Orders application temporarily depends on `Cinema.Application` for those contracts and the shared context. Payment, loyalty, Booking locks, RabbitMQ messages, SignalR notifications, and Hangfire behavior are unchanged.
