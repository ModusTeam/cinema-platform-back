namespace Cinema.Domain.Exceptions;

public class PriceNotConfiguredException : DomainException
{
    public PriceNotConfiguredException(string pricingName, string seatTypeName, DateTime sessionTime)
        : base($"Price is not configured for pricing '{pricingName}', seat type '{seatTypeName}' at {sessionTime}.")
    {
    }
}