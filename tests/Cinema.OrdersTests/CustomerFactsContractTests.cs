using System.Text.Json;
using System.Text.Json.Serialization;
using Cinema.Application.Common.Contracts;
using Cinema.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace Cinema.OrdersTests;

public sealed class CustomerFactsContractTests
{
    private static readonly Guid EventId = Guid.Parse("ABCDEF12-3456-4789-ABCD-123456789ABC");
    private static readonly Guid UserId = Guid.Parse("12345678-ABCD-4123-ABCD-123456789ABC");

    [Fact]
    public void Registered_Uses_Source_Instant_Canonical_Ids_And_Explicit_Nulls()
    {
        User user = new() { Id = UserId };
        user.RecordRegistration(new DateTime(2024, 2, 29, 22, 30, 0, DateTimeKind.Utc).AddTicks(1234));
        CustomerFactsEnvelope envelope = CustomerFactsEnvelope.FromUser(user, EventId, "Registered");
        string json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("pattern").GetString().Should().Be("CUSTOMER_FACTS_CHANGED");
        JsonElement data = document.RootElement.GetProperty("data");
        data.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        data.GetProperty("eventId").GetString().Should().Be("abcdef12-3456-4789-abcd-123456789abc");
        data.GetProperty("userId").GetString().Should().Be("12345678-abcd-4123-abcd-123456789abc");
        data.GetProperty("revision").GetInt32().Should().Be(1);
        data.GetProperty("kind").GetString().Should().Be("Registered");
        data.GetProperty("registeredAtUtc").GetString().Should().Be("2024-02-29T22:30:00.000Z");
        data.GetProperty("occurredAtUtc").GetString().Should().Be("2024-02-29T22:30:00.000Z");
        foreach (string field in new[] { "dobFirstSetAtUtc", "dateOfBirth", "previousDateOfBirth", "correctionOperationId" })
            data.GetProperty(field).ValueKind.Should().Be(JsonValueKind.Null);
        CustomerFactsEnvelope.FromJson(json).Should().Be(envelope);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void First_Dob_Preserves_Leap_Date_And_Truthful_Registration(bool registered, int revision)
    {
        User user = new() { Id = UserId };
        if (registered) user.RecordRegistration(new DateTime(2024, 2, 29, 22, 30, 0, DateTimeKind.Utc));
        user.RecordFirstDateOfBirth(new DateOnly(1992, 2, 29), new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc));
        CustomerFactsEnvelope envelope = CustomerFactsEnvelope.FromUser(user, EventId, "DateOfBirthSet");
        envelope.Data.Revision.Should().Be(revision);
        envelope.Data.DateOfBirth.Should().Be("1992-02-29");
        envelope.Data.DobFirstSetAtUtc.Should().Be("2024-03-01T10:00:00.000Z");
        envelope.Data.RegisteredAtUtc.Should().Be(registered ? "2024-02-29T22:30:00.000Z" : null);
        CustomerFactsEnvelope.FromJson(envelope.ToJson()).Should().Be(envelope);
        Action repeat = () => user.RecordFirstDateOfBirth(new DateOnly(1990, 1, 1), DateTime.UtcNow);
        repeat.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("1992-02-29", "1991-02-29")]
    [InlineData(".000Z", "Z")]
    [InlineData(".000Z", ".0000000Z")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [InlineData("\"previousDateOfBirth\":null,", "")]
    public void Snapshot_Rejects_Invalid_Or_Noncanonical_Wire_Values(string original, string invalid)
    {
        User user = new() { Id = UserId };
        user.RecordFirstDateOfBirth(new DateOnly(1992, 2, 29), new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc));
        string json = CustomerFactsEnvelope.FromUser(user, EventId, "DateOfBirthSet").ToJson();
        Action parse = () => CustomerFactsEnvelope.FromJson(json.Replace(original, invalid));
        parse.Should().Throw<JsonException>();
    }

    [Fact]
    public void Correction_Wire_Fixture_Remains_Supported_Without_A_Correction_Operation()
    {
        const string json = """
        {"pattern":"CUSTOMER_FACTS_CHANGED","data":{"schemaVersion":1,"eventId":"ABCDEF12-3456-4789-ABCD-123456789ABC",
        "userId":"12345678-ABCD-4123-ABCD-123456789ABC","revision":3,"kind":"DateOfBirthCorrected",
        "occurredAtUtc":"2024-04-01T10:00:00.000Z","registeredAtUtc":"2024-02-29T22:30:00.000Z",
        "dobFirstSetAtUtc":"2024-03-01T10:00:00.000Z","dateOfBirth":"1990-10-21","previousDateOfBirth":"1992-02-29",
        "correctionOperationId":"ABCDEF12-3456-4789-ABCD-123456789ABD"}}
        """;
        CustomerFactsEnvelope envelope = CustomerFactsEnvelope.FromJson(json);
        envelope.Data.Kind.Should().Be("DateOfBirthCorrected");
        envelope.ToJson().Should().Contain("abcdef12-3456-4789-abcd-123456789abd");
        CustomerFactsEnvelope.FromJson(envelope.ToJson()).Should().Be(envelope);
    }

    [Fact]
    public void Source_Instants_Reject_Unspecified_Time_And_Registration_Cannot_Reset()
    {
        User user = new() { Id = UserId };
        Action unspecified = () => user.RecordRegistration(new DateTime(2024, 1, 1));
        unspecified.Should().Throw<ArgumentException>();
        user.RecordRegistration(DateTime.UtcNow);
        Action reset = () => user.RecordRegistration(DateTime.UtcNow);
        reset.Should().Throw<InvalidOperationException>();
    }
}
