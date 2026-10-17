namespace Cinema.Domain.Enums;

public enum OrderStatus
{
    Pending = 0,
    Paid = 1,
    Failed = 2,
    Cancelled = 3,
    PaymentProcessing = 4,
    PaymentOutcomeUnknown = 5,
    Expired = 6
}
