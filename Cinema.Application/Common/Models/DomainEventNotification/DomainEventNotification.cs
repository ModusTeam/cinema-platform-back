using Cinema.Domain.Interfaces;
using MediatR;

namespace Cinema.Application.Common.Models.DomainEventNotification;

public class DomainEventNotification<TDomainEvent>(TDomainEvent domainEvent) : INotification 
    where TDomainEvent : IDomainEvent
{
    public TDomainEvent DomainEvent { get; } = domainEvent;
}