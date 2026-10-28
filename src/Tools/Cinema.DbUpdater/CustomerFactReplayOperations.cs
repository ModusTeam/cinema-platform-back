using Cinema.Application.Common.Contracts;
using Cinema.Domain.Entities;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Services;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinema.DbUpdater;

internal static class CustomerFactReplayOperations
{
    public static async Task<int> RunAsync(string connection, IConfiguration configuration, Guid eventId, ILogger logger, CancellationToken ct)
    {
        await using var source = DatabaseContextConfiguration.CreateDataSource(connection);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => DatabaseContextConfiguration.Configure(options, source));
        services.AddScoped<CustomerFactsWriter>();
        // Only the scoped DB outbox is used: do not start a bus/delivery host here.
        // The API's existing delivery service sends the committed transport record.
        services.AddMassTransit(registration =>
        {
            registration.AddEntityFrameworkOutbox<ApplicationDbContext>(outbox =>
            {
                outbox.UsePostgres(); outbox.UseBusOutbox();
            });
            registration.UsingRabbitMq((_, bus) =>
            {
                bus.Host(configuration["RabbitMq:Host"] ?? "localhost", 5672,
                    configuration["RabbitMq:VirtualHost"] ?? "/", host =>
                    {
                        host.Username(configuration["RabbitMq:Username"] ?? "guest");
                        host.Password(configuration["RabbitMq:Password"] ?? "guest");
                    });
                bus.UseRawJsonSerializer();
            });
        });
        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        CustomerFactRevision? fact = await db.CustomerFactRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.EventId == eventId, ct);
        if (fact is null)
        {
            logger.LogError("CUSTOMER_FACT_NOT_FOUND EventId {EventId}", eventId);
            return 2;
        }
        CustomerFactsEnvelope envelope = CustomerFactsEnvelope.FromJson(fact.Snapshot);
        await scope.ServiceProvider.GetRequiredService<CustomerFactsWriter>().ExecuteAsync(fact.UserId, async token =>
        {
            ISendEndpoint endpoint = await scope.ServiceProvider.GetRequiredService<ISendEndpointProvider>()
                .GetSendEndpoint(new Uri("queue:loyalty_ticket_purchased"));
            await endpoint.Send(envelope, send => send.MessageId = fact.EventId, token);
            return Cinema.Domain.Shared.Result.Success();
        }, ct);
        logger.LogInformation("Customer fact restaged: {EventId}", eventId);
        return 0;
    }
}
