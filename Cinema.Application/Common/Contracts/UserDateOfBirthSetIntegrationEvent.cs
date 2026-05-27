namespace Cinema.Application.Common.Contracts;

/// <summary>
/// Integration event published to RabbitMQ after a user sets their date of birth for the first time.
/// Consumed by the Loyalty Service to handle birthday-related bonuses.
/// </summary>
public record UserDateOfBirthSetIntegrationEvent(
    Guid UserId,
    DateOnly DateOfBirth,
    DateTime OccurredAtUtc);
