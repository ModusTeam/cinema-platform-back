using Cinema.Domain.Entities;
using Cinema.Domain.Interfaces;

namespace Cinema.Domain.Events;

public record OrderCancelledDomainEvent(Order Order) : IDomainEvent;
