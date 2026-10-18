using Cinema.Application.Common.Models.DomainEventNotification;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Persistence.Interceptors;

public class DispatchDomainEventsInterceptor(IPublisher publisher, ILogger<DispatchDomainEventsInterceptor>? logger = null)
    : SaveChangesInterceptor, IPostCommitDomainEventDispatcher
{
    private readonly List<INotification> _postCommitNotifications = [];
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        DispatchDomainEvents(eventData.Context).GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await DispatchDomainEvents(eventData.Context, cancellationToken);
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public async Task DispatchDomainEvents(DbContext? context, CancellationToken cancellationToken = default)
    {
        if (context == null) return;

        var entities = context.ChangeTracker
            .Entries<BaseEntity>()
            .Where(e => e.Entity.DomainEvents.Any())
            .Select(e => e.Entity)
            .ToList();

        var domainEvents = entities
            .SelectMany(e => e.DomainEvents)
            .ToList();

        entities.ForEach(e => e.ClearDomainEvents());

        foreach (var domainEvent in domainEvents)
        {
            await publisher.Publish(domainEvent, cancellationToken);

            var notificationType = typeof(DomainEventNotification<>).MakeGenericType(domainEvent.GetType());
            var notification = (INotification)Activator.CreateInstance(notificationType, domainEvent)!;
            _postCommitNotifications.Add(notification);
        }
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        PublishPostCommitNotifications(eventData.Context).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await PublishPostCommitNotifications(eventData.Context, cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        _postCommitNotifications.Clear();
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _postCommitNotifications.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private async Task PublishPostCommitNotifications(DbContext? context, CancellationToken cancellationToken = default)
    {
        if (context?.Database.CurrentTransaction is not null)
            return;

        await DispatchAfterCommitAsync(cancellationToken);
    }

    public async Task DispatchAfterCommitAsync(CancellationToken cancellationToken)
    {
        List<INotification> notifications = [.. _postCommitNotifications];
        _postCommitNotifications.Clear();

        foreach (INotification notification in notifications)
        {
            try
            {
                await publisher.Publish(notification, cancellationToken);
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Post-commit notification {NotificationType} failed", notification.GetType().Name);
            }
        }
    }
}
