using Cinema.Application.Common.Contracts;

namespace Cinema.Application.Common.Interfaces;

public interface ITicketGenerator
{
    byte[] GenerateTicketPdf(TicketPurchasedMessage ticketData);
}