using Cinema.Infrastructure.Persistence;
using MassTransit;

namespace Cinema.Infrastructure.Messaging.Consumers;

public class TicketPurchasedConsumerDefinition : ConsumerDefinition<TicketPurchasedConsumer>
{
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<TicketPurchasedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseEntityFrameworkOutbox<ApplicationDbContext>(context);
    }
}
