namespace Cinema.Domain.Enums;

public enum GoldCheckoutState
{
    None,
    UsePending,
    Consumed,
    CompensationPending,
    Completed,
    Compensated,
    Rejected
}
