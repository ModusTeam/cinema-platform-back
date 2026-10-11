using Cinema.Domain.Entities;
using Cinema.Domain.Interfaces;

namespace Cinema.Domain.Events;

public record OrderPaidEvent(Order Order) : IDomainEvent;