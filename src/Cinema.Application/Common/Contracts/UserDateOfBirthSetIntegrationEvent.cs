using System.Text.Json.Serialization;

namespace Cinema.Application.Common.Contracts;

/// <summary>
/// NestJS-compatible RabbitMQ envelope sent after a user sets their date of birth.
/// Consumed by the Loyalty service to store DOB and grant birthday bonuses.
/// </summary>
public static class LoyaltyEventPatterns
{
    public const string UserDateOfBirthSet = "USER_DATE_OF_BIRTH_SET";
}

public record UserDateOfBirthSetPayload(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("dateOfBirth")] string DateOfBirth,
    [property: JsonPropertyName("occurredAtUtc")] DateTime OccurredAtUtc);

public record NestJsUserDateOfBirthSetEvent(
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("data")] UserDateOfBirthSetPayload Data);
