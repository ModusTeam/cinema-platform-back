using Cinema.Infrastructure.Persistence;
using MassTransit;
using Npgsql;

namespace Cinema.Infrastructure.Messaging.Consumers;

public class TicketPurchasedConsumerDefinition : ConsumerDefinition<TicketPurchasedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<TicketPurchasedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(retry =>
        {
            retry.Handle<PostgresException>(exception =>
                exception.SqlState == PostgresErrorCodes.SerializationFailure);
            retry.Interval(3, TimeSpan.FromSeconds(1));
        });
        endpointConfigurator.UseEntityFrameworkOutbox<ApplicationDbContext>(context);
    }
}
