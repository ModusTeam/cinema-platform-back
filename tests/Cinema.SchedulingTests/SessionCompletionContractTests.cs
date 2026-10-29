using System.Globalization;
using System.Text.Json;
using Cinema.Application.Common.Contracts;
using FluentAssertions;
using Xunit;
namespace Cinema.SchedulingTests;
public class SessionCompletionContractTests
{
    [Fact]
    public void Production_envelope_matches_merged_Loyalty_fixtures_exactly()
    {
        using JsonDocument fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/session-completed-v1.json")));
        foreach (JsonElement envelope in fixtures.RootElement.EnumerateArray())
        {
            JsonElement data = envelope.GetProperty("data");
            DateTime end = DateTime.ParseExact(data.GetProperty("completedAtUtc").GetString()!,
                "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal);
            SessionCompletedEnvelope actual = SessionCompletedEnvelope.Create(data.GetProperty("eventId").GetGuid(),data.GetProperty("sessionId").GetGuid(),end);
            using JsonDocument json = JsonDocument.Parse(actual.ToJson());
            JsonElement.DeepEquals(envelope,json.RootElement).Should().BeTrue();
        }
    }
}
