using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cinema.Domain.Entities;

namespace Cinema.Application.Common.Contracts;

public sealed record CustomerFactsData(
    [property: JsonPropertyName("schemaVersion"), JsonRequired] int SchemaVersion,
    [property: JsonPropertyName("eventId"), JsonRequired] Guid EventId,
    [property: JsonPropertyName("userId"), JsonRequired] Guid UserId,
    [property: JsonPropertyName("revision"), JsonRequired] int Revision,
    [property: JsonPropertyName("kind"), JsonRequired] string Kind,
    [property: JsonPropertyName("occurredAtUtc"), JsonRequired] string OccurredAtUtc,
    [property: JsonPropertyName("registeredAtUtc"), JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RegisteredAtUtc,
    [property: JsonPropertyName("dobFirstSetAtUtc"), JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DobFirstSetAtUtc,
    [property: JsonPropertyName("dateOfBirth"), JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DateOfBirth,
    [property: JsonPropertyName("previousDateOfBirth"), JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PreviousDateOfBirth,
    [property: JsonPropertyName("correctionOperationId"), JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? CorrectionOperationId);

public sealed record CustomerFactsEnvelope(
    [property: JsonPropertyName("pattern"), JsonRequired] string Pattern,
    [property: JsonPropertyName("data"), JsonRequired] CustomerFactsData Data)
{
    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static CustomerFactsEnvelope FromUser(User user, Guid eventId, string kind)
    {
        DateTime? occurred = kind switch
        {
            "Registered" => user.RegisteredAtUtc,
            "DateOfBirthSet" => user.DobFirstSetAtUtc,
            _ => throw new InvalidOperationException("Unsupported source operation")
        };
        if (!occurred.HasValue) throw new InvalidOperationException("Missing authoritative source instant");
        CustomerFactsEnvelope result = new("CUSTOMER_FACTS_CHANGED", new CustomerFactsData(
            1, eventId, user.Id, user.CustomerFactsRevision, kind, Instant(occurred.Value),
            user.RegisteredAtUtc is { } registered ? Instant(registered) : null,
            user.DobFirstSetAtUtc is { } first ? Instant(first) : null,
            user.DateOfBirth is { } dob ? DateOnly.FromDateTime(dob).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            null, null));
        result.Validate();
        return result;
    }

    public string ToJson()
    {
        Validate();
        return JsonSerializer.Serialize(this);
    }

    public static CustomerFactsEnvelope FromJson(string snapshot)
    {
        CustomerFactsEnvelope envelope = JsonSerializer.Deserialize<CustomerFactsEnvelope>(snapshot)
            ?? throw new JsonException("Invalid customer facts envelope");
        envelope.Validate();
        return envelope;
    }

    private static string Instant(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC source instant required");
        return value.ToString(InstantFormat, CultureInfo.InvariantCulture);
    }

    private static bool IsInstant(string? value) => value is not null && DateTime.TryParseExact(
        value, InstantFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);
    private static bool IsDate(string? value) => value is not null && DateOnly.TryParseExact(
        value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private void Validate()
    {
        if (Pattern != "CUSTOMER_FACTS_CHANGED" || Data is null || Data.SchemaVersion != 1 || Data.Revision < 1 ||
            !IsInstant(Data.OccurredAtUtc) || Data.RegisteredAtUtc is not null && !IsInstant(Data.RegisteredAtUtc) ||
            Data.DobFirstSetAtUtc is not null && !IsInstant(Data.DobFirstSetAtUtc) ||
            Data.DateOfBirth is not null && !IsDate(Data.DateOfBirth) ||
            Data.PreviousDateOfBirth is not null && !IsDate(Data.PreviousDateOfBirth))
            throw new JsonException("Invalid customer facts envelope");
        bool valid = Data.Kind switch
        {
            "Registered" => Data.Revision == 1 && Data.RegisteredAtUtc == Data.OccurredAtUtc &&
                Data.DateOfBirth is null && Data.DobFirstSetAtUtc is null && Data.PreviousDateOfBirth is null && Data.CorrectionOperationId is null,
            "DateOfBirthSet" => Data.DateOfBirth is not null && Data.DobFirstSetAtUtc == Data.OccurredAtUtc &&
                Data.PreviousDateOfBirth is null && Data.CorrectionOperationId is null,
            "DateOfBirthCorrected" => Data.DateOfBirth is not null && Data.PreviousDateOfBirth is not null &&
                Data.DateOfBirth != Data.PreviousDateOfBirth && Data.CorrectionOperationId is not null,
            _ => false
        };
        if (!valid) throw new JsonException("Invalid customer facts transition");
    }
}
